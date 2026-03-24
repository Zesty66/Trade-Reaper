#!/usr/bin/env python3
"""
============================================================
  TRADE REAPER — Dashboard + Bridge Server + Account Manager v3.0
============================================================
  - HTTP bridge on port 8777 (receives data from browser scraper)
  - Saves levels_data.json and levels_flat.csv
  - Desktop dashboard (tkinter) showing ALL live data
  - Account Manager tab to add/edit/remove trading profiles
============================================================
"""

import json
import os
import sys
import time
import threading
import tempfile
import shutil
from datetime import datetime, timezone
from http.server import HTTPServer, BaseHTTPRequestHandler
from pathlib import Path

# --- Try to import tkinter (only works on Windows/Mac with display) ---
try:
    import tkinter as tk
    from tkinter import font as tkfont
    from tkinter import ttk, messagebox
    HAS_TK = True
except ImportError:
    HAS_TK = False

# --- Try to import PIL for logo support ---
try:
    from PIL import Image, ImageTk
    HAS_PIL = True
except ImportError:
    HAS_PIL = False

# ============================================================
# CONFIG
# ============================================================
BRIDGE_PORT = 8777
DATA_DIR = Path(__file__).parent
JSON_FILE = DATA_DIR / "levels_data.json"
CSV_FILE = DATA_DIR / "levels_flat.csv"
ACCOUNTS_FILE = DATA_DIR / "accounts_config.json"
PRESSURE_FILE = DATA_DIR / "pressure_lines.json"
STALE_TIMEOUT = 15  # seconds before "STALE" warning

# ============================================================
# SHARED STATE
# ============================================================
latest_data = {}

# Pressure lines (manually entered, saved to file)
pressure_lines = {
    "NQ": {"yellow": [], "red": []},
    "ES": {"yellow": [], "red": []}
}
last_receive_time = 0
receive_count = 0
lock = threading.Lock()


# ============================================================
# ACCOUNTS CONFIG
# ============================================================
def load_accounts():
    """Load account profiles from JSON config."""
    if not ACCOUNTS_FILE.exists():
        return []
    try:
        with open(ACCOUNTS_FILE) as f:
            data = json.load(f)
        return data.get("accounts", [])
    except Exception as e:
        print(f"[ERROR] Loading accounts: {e}")
        return []


def save_accounts(accounts):
    """Save account profiles to JSON config (atomic write)."""
    data = {"accounts": accounts}
    tmp = str(ACCOUNTS_FILE) + ".tmp"
    try:
        with open(tmp, "w") as f:
            json.dump(data, f, indent=2)
        shutil.move(tmp, str(ACCOUNTS_FILE))
        return True
    except Exception as e:
        print(f"[ERROR] Saving accounts: {e}")
        return False


def load_pressure_lines():
    """Load pressure lines from JSON file."""
    global pressure_lines
    if PRESSURE_FILE.exists():
        try:
            with open(PRESSURE_FILE) as f:
                pressure_lines = json.load(f)
        except Exception:
            pass
    return pressure_lines


def save_pressure_lines():
    """Save pressure lines to JSON file."""
    tmp = str(PRESSURE_FILE) + ".tmp"
    try:
        with open(tmp, "w") as f:
            json.dump(pressure_lines, f, indent=2)
        shutil.move(tmp, str(PRESSURE_FILE))
        return True
    except Exception:
        return False


DEFAULT_ACCOUNT = {
    "name": "",
    "enabled": True,
    "accountSize": 100000,
    "instrument": "NQ",
    "qty": 1,
    "dailyLossLimit": 500,
    "dailyProfitTarget": 1000,
    "allowEST": True,
    "allowAPlus": True,
    "allowA": True,
    "allowBPlus": False,
    "allowZones": True,
    "allowArbys": True,
    "allowMHP": True,
    "allowHP": True,
    "allowDDBand": True,
    "skipFlip": True,
    "staleDataCutoff": 30,
    "stopMode": "trailing",
    "notes": ""
}


# ============================================================
# FILE WRITERS (atomic write: .tmp → rename)
# ============================================================
def save_json(data):
    """Save full JSON data atomically."""
    tmp = str(JSON_FILE) + ".tmp"
    try:
        with open(tmp, "w") as f:
            json.dump(data, f, indent=2)
        shutil.move(tmp, str(JSON_FILE))
    except Exception as e:
        print(f"[ERROR] JSON save failed: {e}")


def save_csv(data):
    """Save flat CSV for NinjaTrader consumption, atomically."""
    rows = []
    rows.append("instrument,levelType,price,extraInfo")

    chart_levels = data.get("chartLevels", {})

    for instrument in ["NQ", "ES"]:
        lvl = chart_levels.get(instrument)
        if not lvl:
            continue

        if lvl.get("hp") is not None:
            rows.append(f"{instrument},HP,{lvl['hp']},")
        if lvl.get("mhp") is not None:
            rows.append(f"{instrument},MHP,{lvl['mhp']},")
        if lvl.get("hg") is not None:
            rows.append(f"{instrument},HG,{lvl['hg']},")
        if lvl.get("open") is not None:
            rows.append(f"{instrument},OPEN,{lvl['open']},")
        if lvl.get("close") is not None:
            rows.append(f"{instrument},CLOSE,{lvl['close']},")

        dd = lvl.get("dd", {})
        if dd.get("upper") is not None:
            rows.append(f"{instrument},DD_UPPER,{dd['upper']},")
        if dd.get("lower") is not None:
            rows.append(f"{instrument},DD_LOWER,{dd['lower']},")

        for i, zone in enumerate(lvl.get("bullZones", []), 1):
            rows.append(f"{instrument},BULL_ZONE_BOT,{zone['bottom']},{i}")
            rows.append(f"{instrument},BULL_ZONE_TOP,{zone['top']},{i}")

        for i, zone in enumerate(lvl.get("bearZones", []), 1):
            rows.append(f"{instrument},BEAR_ZONE_BOT,{zone['bottom']},{i}")
            rows.append(f"{instrument},BEAR_ZONE_TOP,{zone['top']},{i}")

    sp_res = data.get("sp500Resilience", {})
    if sp_res.get("res") is not None:
        rows.append(f"SP500,RES,{sp_res['res']},")
    if sp_res.get("sp") is not None:
        rows.append(f"SP500,RES_SP,{sp_res['sp']},")
    if sp_res.get("hp") is not None:
        rows.append(f"SP500,RES_HP,{sp_res['hp']},")

    nq_res = data.get("nq100Resilience", {})
    if nq_res.get("res") is not None:
        rows.append(f"NQ100,RES,{nq_res['res']},")
    if nq_res.get("sp") is not None:
        rows.append(f"NQ100,RES_SP,{nq_res['sp']},")
    if nq_res.get("hp") is not None:
        rows.append(f"NQ100,RES_HP,{nq_res['hp']},")

    if data.get("ddRatio") is not None:
        rows.append(f"MARKET,DD_RATIO,{data['ddRatio']},")

    for sym, price in data.get("livePrice", {}).items():
        direction = data.get("alertDirection", {}).get(sym, "")
        rows.append(f"{sym},LIVE_PRICE,{price},{direction}")

    for sym, val in data.get("headerHP", {}).items():
        rows.append(f"{sym},HEADER_HP,{val},")
    for sym, val in data.get("headerMHP", {}).items():
        rows.append(f"{sym},HEADER_MHP,{val},")

    # Pressure lines (from manual input)
    for instrument in ["NQ", "ES"]:
        pl = pressure_lines.get(instrument, {})
        for val in pl.get("yellow", []):
            rows.append(f"{instrument},YELLOW_PRESSURE,{val},")
        for val in pl.get("red", []):
            rows.append(f"{instrument},RED_PRESSURE,{val},")

    tmp = str(CSV_FILE) + ".tmp"
    try:
        with open(tmp, "w") as f:
            f.write("\n".join(rows) + "\n")
        shutil.move(tmp, str(CSV_FILE))
    except Exception as e:
        print(f"[ERROR] CSV save failed: {e}")


# ============================================================
# HTTP BRIDGE SERVER
# ============================================================
class BridgeHandler(BaseHTTPRequestHandler):
    def do_POST(self):
        global latest_data, last_receive_time, receive_count
        try:
            length = int(self.headers.get("Content-Length", 0))
            body = self.rfile.read(length)
            data = json.loads(body)

            data["serverTime"] = datetime.now().isoformat()

            with lock:
                latest_data = data
                last_receive_time = time.time()
                receive_count += 1

            save_json(data)
            save_csv(data)

            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Access-Control-Allow-Origin", "*")
            self.end_headers()
            self.wfile.write(b'{"status":"ok"}')

        except Exception as e:
            print(f"[ERROR] Bridge: {e}")
            self.send_response(500)
            self.end_headers()
            self.wfile.write(str(e).encode())

    def do_OPTIONS(self):
        self.send_response(200)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")
        self.end_headers()

    def log_message(self, format, *args):
        pass


def run_bridge():
    server = HTTPServer(("0.0.0.0", BRIDGE_PORT), BridgeHandler)
    print(f"[TradeReaper] Bridge server listening on port {BRIDGE_PORT}")
    server.serve_forever()


# ============================================================
# DASHBOARD (tkinter)
# ============================================================
if HAS_TK:

    class Dashboard:
        # Modern dark palette
        BG = "#0f0f0f"
        CARD_BG = "#1a1a1a"
        TEXT = "#e4e4e7"
        GREEN = "#34d399"
        RED = "#f87171"
        YELLOW = "#fbbf24"
        BLUE = "#60a5fa"
        ORANGE = "#fb923c"
        DIM = "#71717a"
        HEADER_BG = "#18181b"
        BTN_BG = "#27272a"
        BTN_HOVER = "#3f3f46"
        ACCENT = "#a78bfa"         # soft purple accent
        BORDER = "#27272a"
        INPUT_BG = "#111111"

        def __init__(self):
            self.root = tk.Tk()
            self.root.title("Trade Reaper v3.0")
            self.root.configure(bg=self.BG)
            self.root.geometry("1050x870")
            self.root.minsize(950, 750)

            # Set window icon
            icon_path = DATA_DIR / "logo.png"
            if icon_path.exists():
                try:
                    if HAS_PIL:
                        ico = ImageTk.PhotoImage(Image.open(icon_path))
                        self.root.iconphoto(True, ico)
                        self._icon_ref = ico  # keep reference
                    else:
                        ico = tk.PhotoImage(file=str(icon_path))
                        self.root.iconphoto(True, ico)
                        self._icon_ref = ico
                except Exception:
                    pass  # icon is cosmetic, don't crash

            # Fonts — modern sans-serif with monospace for data
            self.font_title = tkfont.Font(family="Segoe UI", size=18, weight="bold")
            self.font_header = tkfont.Font(family="Segoe UI Semibold", size=11, weight="bold")
            self.font_label = tkfont.Font(family="Cascadia Code", size=9)
            self.font_value = tkfont.Font(family="Cascadia Code", size=12, weight="bold")
            self.font_small = tkfont.Font(family="Segoe UI", size=8)
            self.font_zone = tkfont.Font(family="Cascadia Code", size=9)
            self.font_btn = tkfont.Font(family="Segoe UI Semibold", size=10, weight="bold")

            self._build_ui()
            self._update_loop()

        def _build_ui(self):
            # --- Title bar ---
            title_frame = tk.Frame(self.root, bg=self.HEADER_BG, pady=10)
            title_frame.pack(fill="x")

            # Subtle bottom border line
            tk.Frame(self.root, bg=self.BORDER, height=1).pack(fill="x")

            # Logo image next to title
            self._logo_image = None
            logo_path = DATA_DIR / "logo.png"
            if logo_path.exists():
                try:
                    if HAS_PIL:
                        img = Image.open(logo_path)
                        img = img.resize((36, 36), Image.LANCZOS)
                        self._logo_image = ImageTk.PhotoImage(img)
                    else:
                        self._logo_image = tk.PhotoImage(file=str(logo_path))
                        w = self._logo_image.width()
                        if w > 36:
                            factor = max(1, w // 36)
                            self._logo_image = self._logo_image.subsample(factor)
                except Exception as e:
                    print(f"[WARN] Could not load logo: {e}")

            if self._logo_image:
                tk.Label(title_frame, image=self._logo_image,
                         bg=self.HEADER_BG).pack(side="left", padx=(18, 8))
                tk.Label(title_frame, text="Trade Reaper", font=self.font_title,
                         fg=self.TEXT, bg=self.HEADER_BG).pack(side="left", padx=(0, 6))
            else:
                tk.Label(title_frame, text="Trade Reaper", font=self.font_title,
                         fg=self.TEXT, bg=self.HEADER_BG).pack(side="left", padx=18)

            # Version badge
            tk.Label(title_frame, text="v3.0", font=self.font_small,
                     fg=self.DIM, bg=self.HEADER_BG).pack(side="left", pady=(6, 0))

            self.status_label = tk.Label(title_frame, text="  WAITING  ",
                                         font=self.font_small, fg=self.BG, bg=self.YELLOW,
                                         padx=8, pady=2)
            self.status_label.pack(side="right", padx=18)
            self.count_label = tk.Label(title_frame, text="",
                                        font=self.font_small, fg=self.DIM, bg=self.HEADER_BG)
            self.count_label.pack(side="right", padx=5)

            # --- Tabbed notebook ---
            style = ttk.Style()
            style.theme_use("default")
            style.configure("Dark.TNotebook", background=self.BG, borderwidth=0)
            style.configure("Dark.TNotebook.Tab", background=self.CARD_BG,
                            foreground=self.DIM, padding=[16, 8],
                            font=("Segoe UI", 10, "bold"))
            style.map("Dark.TNotebook.Tab",
                       background=[("selected", self.BTN_BG)],
                       foreground=[("selected", self.TEXT)])

            self.notebook = ttk.Notebook(self.root, style="Dark.TNotebook")
            self.notebook.pack(fill="both", expand=True, padx=5, pady=5)

            # Tab 1: Live Dashboard
            self.dash_tab = tk.Frame(self.notebook, bg=self.BG)
            self.notebook.add(self.dash_tab, text="  Live Dashboard  ")
            self._build_dashboard_tab()

            # Tab 2: Pressure Lines
            self.pressure_tab = tk.Frame(self.notebook, bg=self.BG)
            self.notebook.add(self.pressure_tab, text="  Pressure Lines  ")
            self._build_pressure_tab()

            # Tab 3: Account Manager
            self.acct_tab = tk.Frame(self.notebook, bg=self.BG)
            self.notebook.add(self.acct_tab, text="  Account Manager  ")
            self._build_account_tab()

            # Tab 4: Scraper Code
            self.scraper_tab = tk.Frame(self.notebook, bg=self.BG)
            self.notebook.add(self.scraper_tab, text="  Scraper Code  ")
            self._build_scraper_tab()

            # Tab 5: Notes
            self.notes_tab = tk.Frame(self.notebook, bg=self.BG)
            self.notebook.add(self.notes_tab, text="  Notes  ")
            self._build_notes_tab()

        # =============================================
        # TAB 1: LIVE DASHBOARD
        # =============================================

        def _build_dashboard_tab(self):
            main_frame = tk.Frame(self.dash_tab, bg=self.BG)
            main_frame.pack(fill="both", expand=True, padx=10, pady=5)

            # Top row: Resilience + DD + Prices
            top_row = tk.Frame(main_frame, bg=self.BG)
            top_row.pack(fill="x", pady=3)

            self.sp_res_card = self._make_res_card(top_row, "SP500 RESILIENCE", width=200)
            self.sp_res_card.pack(side="left", fill="both", expand=True, padx=3)

            self.nq_res_card = self._make_res_card(top_row, "NQ100 RESILIENCE", width=200)
            self.nq_res_card.pack(side="left", fill="both", expand=True, padx=3)

            dd_price_frame = tk.Frame(top_row, bg=self.BG)
            dd_price_frame.pack(side="left", fill="both", expand=True, padx=3)

            self.dd_card = self._make_card(dd_price_frame, "DD RATIO", width=130)
            self.dd_card.pack(fill="x", pady=(0, 3))

            self.price_card = self._make_card(dd_price_frame, "LIVE PRICES", width=130)
            self.price_card.pack(fill="x")

            # Middle row: ES Levels + NQ Levels
            mid_row = tk.Frame(main_frame, bg=self.BG)
            mid_row.pack(fill="both", expand=True, pady=3)

            self.es_card = self._make_card(mid_row, "ES LEVELS", width=450)
            self.es_card.pack(side="left", fill="both", expand=True, padx=3)

            self.nq_card = self._make_card(mid_row, "NQ LEVELS", width=450)
            self.nq_card.pack(side="left", fill="both", expand=True, padx=3)

            # Bottom: Activity Log
            bot_row = tk.Frame(main_frame, bg=self.BG)
            bot_row.pack(fill="x", pady=3)
            self.log_card = self._make_card(bot_row, "ACTIVITY LOG", width=900)
            self.log_card.pack(fill="x", padx=3)

        # =============================================
        # TAB 2: PRESSURE LINES
        # =============================================

        def _build_pressure_tab(self):
            load_pressure_lines()

            # Instructions
            toolbar = tk.Frame(self.pressure_tab, bg=self.HEADER_BG, pady=6)
            toolbar.pack(fill="x")
            tk.Label(toolbar, text="Paste pressure levels (one per line). These are EXIT-ONLY levels from hedging.",
                     font=self.font_small, fg=self.DIM, bg=self.HEADER_BG).pack(side="left", padx=10)
            self._make_button(toolbar, "Save & Apply", self._save_pressure, color=self.GREEN).pack(side="right", padx=8)
            self._make_button(toolbar, "Clear All", self._clear_pressure, color=self.RED).pack(side="right", padx=4)

            # Grid: 4 text boxes (NQ yellow, NQ red, ES yellow, ES red)
            grid = tk.Frame(self.pressure_tab, bg=self.BG)
            grid.pack(fill="both", expand=True, padx=8, pady=8)
            grid.columnconfigure(0, weight=1)
            grid.columnconfigure(1, weight=1)
            grid.rowconfigure(1, weight=1)
            grid.rowconfigure(3, weight=1)

            # NQ row
            tk.Label(grid, text="NQ - Yellow Pressure", font=self.font_header,
                     fg=self.YELLOW, bg=self.BG).grid(row=0, column=0, sticky="w", padx=6, pady=(6, 2))
            tk.Label(grid, text="NQ - Red Pressure", font=self.font_header,
                     fg=self.RED, bg=self.BG).grid(row=0, column=1, sticky="w", padx=6, pady=(6, 2))

            self.nq_yellow_text = tk.Text(grid, bg=self.INPUT_BG, fg=self.YELLOW, insertbackground=self.YELLOW,
                                           font=self.font_label, relief="flat", bd=2, width=30, height=8)
            self.nq_yellow_text.grid(row=1, column=0, sticky="nsew", padx=6, pady=2)

            self.nq_red_text = tk.Text(grid, bg=self.INPUT_BG, fg=self.RED, insertbackground=self.RED,
                                        font=self.font_label, relief="flat", bd=2, width=30, height=8)
            self.nq_red_text.grid(row=1, column=1, sticky="nsew", padx=6, pady=2)

            # ES row
            tk.Label(grid, text="ES - Yellow Pressure", font=self.font_header,
                     fg=self.YELLOW, bg=self.BG).grid(row=2, column=0, sticky="w", padx=6, pady=(10, 2))
            tk.Label(grid, text="ES - Red Pressure", font=self.font_header,
                     fg=self.RED, bg=self.BG).grid(row=2, column=1, sticky="w", padx=6, pady=(10, 2))

            self.es_yellow_text = tk.Text(grid, bg=self.INPUT_BG, fg=self.YELLOW, insertbackground=self.YELLOW,
                                           font=self.font_label, relief="flat", bd=2, width=30, height=8)
            self.es_yellow_text.grid(row=3, column=0, sticky="nsew", padx=6, pady=2)

            self.es_red_text = tk.Text(grid, bg=self.INPUT_BG, fg=self.RED, insertbackground=self.RED,
                                        font=self.font_label, relief="flat", bd=2, width=30, height=8)
            self.es_red_text.grid(row=3, column=1, sticky="nsew", padx=6, pady=2)

            # Load existing values into text boxes
            self._populate_pressure_fields()

        def _populate_pressure_fields(self):
            """Fill text boxes from the current pressure_lines data."""
            for widget, inst, color in [
                (self.nq_yellow_text, "NQ", "yellow"),
                (self.nq_red_text, "NQ", "red"),
                (self.es_yellow_text, "ES", "yellow"),
                (self.es_red_text, "ES", "red"),
            ]:
                widget.delete("1.0", tk.END)
                vals = pressure_lines.get(inst, {}).get(color, [])
                widget.insert("1.0", "\n".join(str(v) for v in vals))

        def _parse_pressure_text(self, widget):
            """Parse numbers from a text widget (one per line)."""
            raw = widget.get("1.0", tk.END).strip()
            if not raw:
                return []
            result = []
            for line in raw.split("\n"):
                line = line.strip().replace(",", "")
                if not line:
                    continue
                try:
                    result.append(float(line))
                except ValueError:
                    pass
            result.sort()
            return result

        def _save_pressure(self):
            global pressure_lines
            pressure_lines["NQ"]["yellow"] = self._parse_pressure_text(self.nq_yellow_text)
            pressure_lines["NQ"]["red"] = self._parse_pressure_text(self.nq_red_text)
            pressure_lines["ES"]["yellow"] = self._parse_pressure_text(self.es_yellow_text)
            pressure_lines["ES"]["red"] = self._parse_pressure_text(self.es_red_text)

            if save_pressure_lines():
                total = sum(len(v) for inst in pressure_lines.values() for v in inst.values())
                messagebox.showinfo("Saved", f"Saved {total} pressure line(s).\nThey'll appear in the next CSV update.")
                # Re-populate to show cleaned/sorted values
                self._populate_pressure_fields()
            else:
                messagebox.showerror("Error", "Failed to save pressure lines!")

        def _clear_pressure(self):
            global pressure_lines
            if messagebox.askyesno("Clear All", "Clear all pressure lines for NQ and ES?"):
                pressure_lines = {"NQ": {"yellow": [], "red": []}, "ES": {"yellow": [], "red": []}}
                save_pressure_lines()
                self._populate_pressure_fields()

        # =============================================
        # TAB 3: ACCOUNT MANAGER
        # =============================================

        def _build_account_tab(self):
            # Toolbar
            toolbar = tk.Frame(self.acct_tab, bg=self.HEADER_BG, pady=6)
            toolbar.pack(fill="x")

            self._make_button(toolbar, "+ New Account", self._new_account).pack(side="left", padx=8)
            self._make_button(toolbar, "Duplicate", self._duplicate_account).pack(side="left", padx=4)
            self._make_button(toolbar, "Delete", self._delete_account, color=self.RED).pack(side="left", padx=4)
            self._make_button(toolbar, "Save All", self._save_all_accounts, color=self.GREEN).pack(side="right", padx=8)

            tk.Label(toolbar, text="Select an account to edit, or add a new one.",
                     font=self.font_small, fg=self.DIM, bg=self.HEADER_BG).pack(side="right", padx=10)

            # Split: account list on left, editor on right
            split = tk.Frame(self.acct_tab, bg=self.BG)
            split.pack(fill="both", expand=True, padx=8, pady=8)

            # Account list
            list_frame = tk.Frame(split, bg=self.CARD_BG, width=250, bd=1, relief="solid",
                                  highlightbackground=self.BORDER, highlightthickness=1)
            list_frame.pack(side="left", fill="y", padx=(0, 6))
            list_frame.pack_propagate(False)

            tk.Label(list_frame, text="ACCOUNTS", font=self.font_header,
                     fg=self.DIM, bg=self.CARD_BG).pack(fill="x", padx=8, pady=6)

            self.acct_listbox = tk.Listbox(list_frame, bg=self.INPUT_BG, fg=self.TEXT,
                                            selectbackground=self.HEADER_BG,
                                            selectforeground=self.GREEN,
                                            font=self.font_label, bd=0,
                                            highlightthickness=0, activestyle="none")
            self.acct_listbox.pack(fill="both", expand=True, padx=4, pady=4)
            self.acct_listbox.bind("<<ListboxSelect>>", self._on_account_select)

            # Editor panel
            editor_outer = tk.Frame(split, bg=self.CARD_BG, bd=1, relief="solid",
                                     highlightbackground=self.BORDER, highlightthickness=1)
            editor_outer.pack(side="left", fill="both", expand=True)

            tk.Label(editor_outer, text="ACCOUNT SETTINGS", font=self.font_header,
                     fg=self.DIM, bg=self.CARD_BG).pack(fill="x", padx=8, pady=6)

            # Scrollable editor
            canvas = tk.Canvas(editor_outer, bg=self.CARD_BG, highlightthickness=0)
            scrollbar = tk.Scrollbar(editor_outer, orient="vertical", command=canvas.yview)
            self.editor_frame = tk.Frame(canvas, bg=self.CARD_BG)

            self.editor_frame.bind("<Configure>",
                lambda e: canvas.configure(scrollregion=canvas.bbox("all")))
            canvas.create_window((0, 0), window=self.editor_frame, anchor="nw")
            canvas.configure(yscrollcommand=scrollbar.set)

            canvas.pack(side="left", fill="both", expand=True)
            scrollbar.pack(side="right", fill="y")

            # Build editor fields
            self.acct_fields = {}
            self._add_field("name", "Profile Name", "entry")
            self._add_field("enabled", "Enabled", "check")
            self._add_field("accountSize", "Account Size ($)", "entry_num")
            self._add_field("instrument", "Instrument", "combo", values=["NQ", "ES"])
            self._add_field("qty", "Trade Quantity", "entry_num")
            self._add_field("dailyLossLimit", "Daily Loss Limit ($)", "entry_num")
            self._add_field("dailyProfitTarget", "Daily Profit Target ($)", "entry_num")
            self._add_field("staleDataCutoff", "Stale Data Cutoff (sec)", "entry_num")
            self._add_field("stopMode", "Stop Mode", "combo",
                            values=["trailing", "breakeven_plus", "fixed"])

            # Separator
            tk.Frame(self.editor_frame, bg=self.BORDER, height=2).pack(fill="x", padx=10, pady=10)
            tk.Label(self.editor_frame, text="GRADE FILTERS", font=self.font_header,
                     fg=self.ACCENT, bg=self.CARD_BG).pack(anchor="w", padx=12)

            self._add_field("allowEST", "Allow EST", "check")
            self._add_field("allowAPlus", "Allow A+", "check")
            self._add_field("allowA", "Allow A", "check")
            self._add_field("allowBPlus", "Allow B+", "check")

            tk.Frame(self.editor_frame, bg=self.BORDER, height=2).pack(fill="x", padx=10, pady=10)
            tk.Label(self.editor_frame, text="SETUP TYPES", font=self.font_header,
                     fg=self.ACCENT, bg=self.CARD_BG).pack(anchor="w", padx=12)

            self._add_field("allowZones", "Zone Trades", "check")
            self._add_field("allowArbys", "Arby's Trades", "check")
            self._add_field("allowMHP", "MHP Trades", "check")
            self._add_field("allowHP", "HP Trades", "check")
            self._add_field("allowDDBand", "DD Band Trades", "check")
            self._add_field("skipFlip", "Skip Flip (flatten, don't reverse)", "check")

            tk.Frame(self.editor_frame, bg=self.BORDER, height=2).pack(fill="x", padx=10, pady=10)
            self._add_field("notes", "Notes", "entry")

            # Load accounts into listbox
            self.accounts = load_accounts()
            self._refresh_account_list()

        def _add_field(self, key, label, ftype, values=None):
            """Add a labeled field to the editor."""
            row = tk.Frame(self.editor_frame, bg=self.CARD_BG)
            row.pack(fill="x", padx=12, pady=3)

            tk.Label(row, text=label, font=self.font_label, fg=self.TEXT,
                     bg=self.CARD_BG, width=24, anchor="w").pack(side="left")

            if ftype == "entry":
                var = tk.StringVar()
                widget = tk.Entry(row, textvariable=var, bg=self.INPUT_BG, fg=self.TEXT,
                                  insertbackground=self.TEXT, font=self.font_label,
                                  relief="flat", bd=2, width=30)
                widget.pack(side="left", fill="x", expand=True)
                self.acct_fields[key] = ("str", var)

            elif ftype == "entry_num":
                var = tk.StringVar()
                widget = tk.Entry(row, textvariable=var, bg=self.INPUT_BG, fg=self.TEXT,
                                  insertbackground=self.TEXT, font=self.font_label,
                                  relief="flat", bd=2, width=15)
                widget.pack(side="left")
                self.acct_fields[key] = ("num", var)

            elif ftype == "check":
                var = tk.BooleanVar()
                widget = tk.Checkbutton(row, variable=var, bg=self.CARD_BG,
                                         fg=self.GREEN, activebackground=self.CARD_BG,
                                         selectcolor="#0d1b2a")
                widget.pack(side="left")
                self.acct_fields[key] = ("bool", var)

            elif ftype == "combo":
                var = tk.StringVar()
                if not values:
                    values = []
                var.set(values[0] if values else "")
                widget = tk.OptionMenu(row, var, *values)
                widget.config(bg=self.INPUT_BG, fg=self.TEXT, font=self.font_label,
                              activebackground=self.HEADER_BG, activeforeground=self.TEXT,
                              highlightthickness=0, relief="flat", width=8)
                widget["menu"].config(bg=self.INPUT_BG, fg=self.TEXT, font=self.font_label,
                                       activebackground=self.ACCENT, activeforeground=self.TEXT)
                widget.pack(side="left")
                self.acct_fields[key] = ("str", var)

        def _make_button(self, parent, text, command, color=None):
            fg = color or self.TEXT
            btn = tk.Button(parent, text=text, command=command,
                            font=self.font_btn, bg=self.BTN_BG,
                            fg=fg,
                            activebackground=self.BTN_HOVER,
                            activeforeground=fg,
                            relief="flat", bd=0, padx=16, pady=6, cursor="hand2",
                            highlightthickness=0)
            # Hover effects
            btn.bind("<Enter>", lambda e, b=btn: b.config(bg=self.BTN_HOVER))
            btn.bind("<Leave>", lambda e, b=btn: b.config(bg=self.BTN_BG))
            return btn

        def _refresh_account_list(self):
            self.acct_listbox.delete(0, tk.END)
            for acct in self.accounts:
                name = acct.get("name", "???")
                inst = acct.get("instrument", "?")
                size = acct.get("accountSize", 0)
                enabled = acct.get("enabled", True)
                status = "" if enabled else " [OFF]"
                display = f"{name} | {inst} | ${size:,.0f}{status}"
                self.acct_listbox.insert(tk.END, display)

        def _on_account_select(self, event=None):
            sel = self.acct_listbox.curselection()
            if not sel:
                return
            idx = sel[0]
            if idx >= len(self.accounts):
                return
            # Don't reload if we already have this account selected (prevents
            # combobox changes from being overwritten by focus-triggered reloads)
            if hasattr(self, '_current_acct_idx') and self._current_acct_idx == idx:
                return
            self._current_acct_idx = idx
            acct = self.accounts[idx]

            # Populate editor fields
            for key, (ftype, var) in self.acct_fields.items():
                val = acct.get(key, DEFAULT_ACCOUNT.get(key, ""))
                if ftype == "bool":
                    var.set(bool(val))
                elif ftype == "num":
                    var.set(str(val))
                else:
                    var.set(str(val))

        def _read_editor(self):
            """Read current editor values into a dict."""
            result = {}
            for key, (ftype, var) in self.acct_fields.items():
                if ftype == "bool":
                    result[key] = var.get()
                elif ftype == "num":
                    raw = var.get().strip()
                    try:
                        result[key] = int(raw) if "." not in raw else float(raw)
                    except ValueError:
                        result[key] = 0
                else:
                    result[key] = var.get().strip()
            return result

        def _save_editor_to_selected(self):
            """Save editor fields back to the selected account."""
            sel = self.acct_listbox.curselection()
            if sel:
                idx = sel[0]
            elif hasattr(self, '_current_acct_idx') and self._current_acct_idx is not None:
                idx = self._current_acct_idx
            else:
                return False
            if idx >= len(self.accounts):
                return False
            self.accounts[idx] = self._read_editor()
            self._refresh_account_list()
            # Re-select
            self.acct_listbox.selection_set(idx)
            self._current_acct_idx = idx
            return True

        def _new_account(self):
            # Save any pending edits first
            if self.acct_listbox.curselection():
                self._save_editor_to_selected()

            new = dict(DEFAULT_ACCOUNT)
            new["name"] = f"Account-{len(self.accounts) + 1}"
            self.accounts.append(new)
            self._refresh_account_list()
            # Select the new one
            idx = len(self.accounts) - 1
            self.acct_listbox.selection_set(idx)
            self._current_acct_idx = None  # force reload
            self._on_account_select()

        def _duplicate_account(self):
            sel = self.acct_listbox.curselection()
            if not sel:
                return
            self._save_editor_to_selected()
            source = dict(self.accounts[sel[0]])
            source["name"] = source["name"] + "-copy"
            self.accounts.append(source)
            self._refresh_account_list()
            idx = len(self.accounts) - 1
            self.acct_listbox.selection_set(idx)
            self._current_acct_idx = None  # force reload
            self._on_account_select()

        def _delete_account(self):
            sel = self.acct_listbox.curselection()
            if not sel:
                return
            idx = sel[0]
            name = self.accounts[idx].get("name", "???")
            if messagebox.askyesno("Delete Account", f"Delete '{name}'?"):
                self.accounts.pop(idx)
                self._current_acct_idx = None  # force reload
                self._refresh_account_list()
                # Clear editor
                for key, (ftype, var) in self.acct_fields.items():
                    if ftype == "bool":
                        var.set(False)
                    else:
                        var.set("")

        def _save_all_accounts(self):
            # Save any pending editor changes first
            if self.acct_listbox.curselection():
                self._save_editor_to_selected()

            if save_accounts(self.accounts):
                messagebox.showinfo("Saved", f"Saved {len(self.accounts)} account(s) to:\n{ACCOUNTS_FILE}")
            else:
                messagebox.showerror("Error", "Failed to save accounts!")

        # =============================================
        # TAB 4: SCRAPER CODE
        # =============================================

        def _build_scraper_tab(self):
            toolbar = tk.Frame(self.scraper_tab, bg=self.HEADER_BG, pady=6)
            toolbar.pack(fill="x")

            tk.Label(toolbar, text="Copy this code and paste into Rocket Scooter's browser console (F12 > Console)",
                     font=self.font_small, fg=self.DIM, bg=self.HEADER_BG).pack(side="left", padx=10)

            self._make_button(toolbar, "Copy to Clipboard", self._copy_scraper, color=self.GREEN).pack(side="right", padx=8)

            self.scraper_status = tk.Label(toolbar, text="", font=self.font_small,
                                            fg=self.GREEN, bg=self.HEADER_BG)
            self.scraper_status.pack(side="right", padx=5)

            # Code display
            code_frame = tk.Frame(self.scraper_tab, bg=self.CARD_BG, bd=1, relief="solid",
                                   highlightbackground=self.BORDER, highlightthickness=1)
            code_frame.pack(fill="both", expand=True, padx=8, pady=8)

            self.scraper_text = tk.Text(code_frame, bg=self.INPUT_BG, fg="#94a3b8",
                                         insertbackground=self.TEXT,
                                         font=("Cascadia Code", 9), relief="flat", bd=4,
                                         wrap="none", state="normal")
            # Scrollbars
            y_scroll = tk.Scrollbar(code_frame, orient="vertical", command=self.scraper_text.yview)
            x_scroll = tk.Scrollbar(code_frame, orient="horizontal", command=self.scraper_text.xview)
            self.scraper_text.configure(yscrollcommand=y_scroll.set, xscrollcommand=x_scroll.set)

            y_scroll.pack(side="right", fill="y")
            x_scroll.pack(side="bottom", fill="x")
            self.scraper_text.pack(fill="both", expand=True)

            # Load the scraper code from file
            scraper_path = DATA_DIR / "rocket_scraper.js"
            try:
                with open(scraper_path) as f:
                    code = f.read()
                self.scraper_text.insert("1.0", code)
            except Exception as e:
                self.scraper_text.insert("1.0", f"// Error loading scraper: {e}\n// Expected at: {scraper_path}")

            self.scraper_text.config(state="disabled")  # Read-only

        def _copy_scraper(self):
            self.scraper_text.config(state="normal")
            code = self.scraper_text.get("1.0", tk.END).strip()
            self.scraper_text.config(state="disabled")
            self.root.clipboard_clear()
            self.root.clipboard_append(code)
            self.scraper_status.config(text="Copied!", fg=self.GREEN)
            # Clear the "Copied!" message after 3 seconds
            self.root.after(3000, lambda: self.scraper_status.config(text=""))

        # =============================================
        # TAB 5: NOTES
        # =============================================

        def _build_notes_tab(self):
            toolbar = tk.Frame(self.notes_tab, bg=self.HEADER_BG, pady=6)
            toolbar.pack(fill="x")

            tk.Label(toolbar, text="Quick notes — contract codes, logins, reminders, etc.",
                     font=self.font_small, fg=self.DIM, bg=self.HEADER_BG).pack(side="left", padx=10)

            self._make_button(toolbar, "Save", self._save_notes, color=self.GREEN).pack(side="right", padx=8)

            self.notes_status = tk.Label(toolbar, text="", font=self.font_small,
                                          fg=self.GREEN, bg=self.HEADER_BG)
            self.notes_status.pack(side="right", padx=5)

            # Text area
            text_frame = tk.Frame(self.notes_tab, bg=self.CARD_BG, bd=0, relief="flat",
                                   highlightbackground=self.BORDER, highlightthickness=1)
            text_frame.pack(fill="both", expand=True, padx=8, pady=8)

            self.notes_text = tk.Text(text_frame, bg=self.INPUT_BG, fg=self.TEXT,
                                       insertbackground=self.TEXT,
                                       font=self.font_label, relief="flat", bd=6,
                                       wrap="word")
            y_scroll = tk.Scrollbar(text_frame, orient="vertical", command=self.notes_text.yview)
            self.notes_text.configure(yscrollcommand=y_scroll.set)
            y_scroll.pack(side="right", fill="y")
            self.notes_text.pack(fill="both", expand=True)

            # Load saved notes
            notes_path = DATA_DIR / "notes.txt"
            if notes_path.exists():
                try:
                    with open(notes_path, encoding="utf-8") as f:
                        self.notes_text.insert("1.0", f.read())
                except Exception:
                    pass

        def _save_notes(self):
            content = self.notes_text.get("1.0", tk.END).rstrip("\n")
            notes_path = DATA_DIR / "notes.txt"
            try:
                tmp = str(notes_path) + ".tmp"
                with open(tmp, "w", encoding="utf-8") as f:
                    f.write(content)
                os.replace(tmp, notes_path)
                self.notes_status.config(text="Saved!", fg=self.GREEN)
                self.root.after(3000, lambda: self.notes_status.config(text=""))
            except Exception as e:
                self.notes_status.config(text=f"Error: {e}", fg=self.RED)

        # =============================================
        # SHARED UI HELPERS
        # =============================================

        def _make_card(self, parent, title, width=200):
            frame = tk.Frame(parent, bg=self.CARD_BG, bd=0, relief="flat",
                             highlightbackground=self.BORDER, highlightthickness=1)
            # Card header with subtle accent line
            header_row = tk.Frame(frame, bg=self.CARD_BG)
            header_row.pack(fill="x", padx=10, pady=(10, 4))
            tk.Frame(header_row, bg=self.ACCENT, width=3, height=14).pack(side="left", padx=(0, 8))
            tk.Label(header_row, text=title, font=self.font_header,
                     fg=self.DIM, bg=self.CARD_BG, anchor="w").pack(side="left")
            content = tk.Label(frame, text="—", font=self.font_label,
                               fg=self.TEXT, bg=self.CARD_BG, anchor="nw",
                               justify="left", wraplength=width - 20)
            content.pack(fill="both", expand=True, padx=14, pady=(0, 10))
            frame._content = content
            return frame

        def _make_res_card(self, parent, title, width=200):
            """Special card for resilience that supports per-line coloring."""
            frame = tk.Frame(parent, bg=self.CARD_BG, bd=0, relief="flat",
                             highlightbackground=self.BORDER, highlightthickness=1)
            header_row = tk.Frame(frame, bg=self.CARD_BG)
            header_row.pack(fill="x", padx=10, pady=(10, 4))
            tk.Frame(header_row, bg=self.ACCENT, width=3, height=14).pack(side="left", padx=(0, 8))
            tk.Label(header_row, text=title, font=self.font_header,
                     fg=self.DIM, bg=self.CARD_BG, anchor="w").pack(side="left")
            content = tk.Text(frame, bg=self.CARD_BG, relief="flat", bd=0,
                              font=self.font_label, height=3, width=25,
                              highlightthickness=0, cursor="arrow")
            content.pack(fill="both", expand=True, padx=14, pady=(0, 10))
            content.config(state="disabled")
            # Pre-configure color tags
            content.tag_configure("main_pos", foreground=self.GREEN)
            content.tag_configure("main_neg", foreground=self.RED)
            content.tag_configure("mhp", foreground=self.ORANGE)
            content.tag_configure("hp", foreground=self.BLUE)
            content.tag_configure("dim", foreground=self.DIM)
            frame._content = content
            return frame

        def _update_resilience(self, card, res_data):
            """Update a resilience card with per-line color coding."""
            widget = card._content
            widget.config(state="normal")
            widget.delete("1.0", "end")

            if not res_data or not isinstance(res_data, dict):
                widget.insert("end", "No data", "dim")
                widget.config(state="disabled")
                return

            main = res_data.get("res")
            mhp = res_data.get("sp")
            hp = res_data.get("hp")

            if main is None and mhp is None and hp is None:
                widget.insert("end", "No data", "dim")
                widget.config(state="disabled")
                return

            if main is not None:
                tag = "main_pos" if main >= 0 else "main_neg"
                widget.insert("end", f"Main:  {main:+.2f}", tag)
            if mhp is not None:
                if main is not None:
                    widget.insert("end", "\n")
                widget.insert("end", f"MHP:   {mhp:+.2f}", "mhp")
            if hp is not None:
                if main is not None or mhp is not None:
                    widget.insert("end", "\n")
                widget.insert("end", f"HP:    {hp:+.2f}", "hp")

            widget.config(state="disabled")

        def _format_levels(self, lvl_data):
            if not lvl_data or not isinstance(lvl_data, dict):
                return "No data", self.DIM

            lines = []
            hp = lvl_data.get("hp")
            mhp = lvl_data.get("mhp")
            hg = lvl_data.get("hg")
            opn = lvl_data.get("open")
            close = lvl_data.get("close")
            dd = lvl_data.get("dd", {})
            dd_upper = dd.get("upper")
            dd_lower = dd.get("lower")

            if hp is not None:     lines.append(f"HP:       {hp:.2f}")
            if mhp is not None:    lines.append(f"MHP:      {mhp:.2f}")
            if hg is not None:     lines.append(f"HG:       {hg:.2f}")
            if opn is not None:    lines.append(f"Open:     {opn:.2f}")
            if close is not None:  lines.append(f"Close:    {close:.2f}")
            if dd_upper is not None: lines.append(f"DD High:  {dd_upper:.2f}")
            if dd_lower is not None: lines.append(f"DD Low:   {dd_lower:.2f}")

            bull_zones = lvl_data.get("bullZones", [])
            if bull_zones:
                lines.append("")
                lines.append(f"Bull Zones ({len(bull_zones)}):")
                for i, z in enumerate(bull_zones, 1):
                    bot = z.get("bottom", 0)
                    top = z.get("top", 0)
                    lines.append(f"  #{i}: {bot:.2f} -> {top:.2f}  ({top-bot:.1f}pts)")

            bear_zones = lvl_data.get("bearZones", [])
            if bear_zones:
                lines.append("")
                lines.append(f"Bear Zones ({len(bear_zones)}):")
                for i, z in enumerate(bear_zones, 1):
                    bot = z.get("bottom", 0)
                    top = z.get("top", 0)
                    lines.append(f"  #{i}: {bot:.2f} -> {top:.2f}  ({top-bot:.1f}pts)")

            if not lines:
                return "No levels", self.DIM

            return "\n".join(lines), self.TEXT

        # =============================================
        # LIVE UPDATE LOOP
        # =============================================

        def _update_loop(self):
            with lock:
                data = latest_data.copy()
                t = last_receive_time
                count = receive_count

            now = time.time()
            age = now - t if t > 0 else 999

            if t == 0:
                self.status_label.config(text="  WAITING  ", fg=self.BG, bg=self.YELLOW)
            elif age < STALE_TIMEOUT:
                self.status_label.config(text="  LIVE  ", fg=self.BG, bg=self.GREEN)
            else:
                self.status_label.config(text=f"  STALE ({int(age)}s)  ", fg="#fff", bg=self.RED)

            self.count_label.config(text=f"#{count}" if count else "")

            if data:
                self._update_resilience(self.sp_res_card, data.get("sp500Resilience"))
                self._update_resilience(self.nq_res_card, data.get("nq100Resilience"))

                dd = data.get("ddRatio")
                if dd is not None:
                    dd_color = self.GREEN if dd >= 0.5 else self.RED if dd <= 0.3 else self.YELLOW
                    self.dd_card._content.config(text=f"{dd:.2f}", fg=dd_color)

                prices = data.get("livePrice", {})
                dirs = data.get("alertDirection", {})
                price_lines = []
                for sym in ["SPY", "QQQ", "IWM"]:
                    p = prices.get(sym)
                    d = dirs.get(sym, "")
                    if p is not None:
                        arrow = "+" if d == "U" else "-" if d == "D" else " "
                        price_lines.append(f"{sym}: {p:.2f} {arrow}")
                if price_lines:
                    self.price_card._content.config(text="\n".join(price_lines))

                es_data = data.get("chartLevels", {}).get("ES")
                es_text, es_color = self._format_levels(es_data)
                self.es_card._content.config(text=es_text, fg=es_color)

                nq_data = data.get("chartLevels", {}).get("NQ")
                nq_text, nq_color = self._format_levels(nq_data)
                self.nq_card._content.config(text=nq_text, fg=nq_color)

                scrape_time = data.get("scrapeTime", "")
                scrape_num = data.get("scrapeCount", 0)
                charts = list(data.get("chartLevels", {}).keys())
                es_zones = nq_zones = 0
                if data.get("chartLevels", {}).get("ES"):
                    es_zones = len(data["chartLevels"]["ES"].get("bullZones", [])) + \
                               len(data["chartLevels"]["ES"].get("bearZones", []))
                if data.get("chartLevels", {}).get("NQ"):
                    nq_zones = len(data["chartLevels"]["NQ"].get("bullZones", [])) + \
                               len(data["chartLevels"]["NQ"].get("bearZones", []))

                acct_count = len(load_accounts())
                log_text = (
                    f"Last scrape: #{scrape_num} at {scrape_time[:19] if scrape_time else '?'}\n"
                    f"Charts: {', '.join(charts) if charts else 'None'} | "
                    f"ES zones: {es_zones} | NQ zones: {nq_zones}\n"
                    f"Accounts configured: {acct_count} | Age: {age:.1f}s"
                )
                self.log_card._content.config(text=log_text)

            self.root.after(1000, self._update_loop)

        def run(self):
            self.root.mainloop()


# ============================================================
# CONSOLE-ONLY DASHBOARD (fallback when no tkinter)
# ============================================================
def console_dashboard():
    print("[TradeReaper] Running in console mode (no GUI)")
    while True:
        time.sleep(5)
        with lock:
            data = latest_data.copy()
            t = last_receive_time
            count = receive_count

        if not data:
            print(f"[{datetime.now().strftime('%H:%M:%S')}] Waiting for data...")
            continue

        age = time.time() - t
        status = "OK" if age < STALE_TIMEOUT else f"STALE({int(age)}s)"

        sp = data.get("sp500Resilience", {})
        nq = data.get("nq100Resilience", {})
        dd = data.get("ddRatio", "?")
        prices = data.get("livePrice", {})

        print(
            f"[{datetime.now().strftime('%H:%M:%S')}] "
            f"#{count} {status} | "
            f"SP Res: {sp.get('res','?')}/{sp.get('sp','?')}/{sp.get('hp','?')} | "
            f"NQ Res: {nq.get('res','?')}/{nq.get('sp','?')}/{nq.get('hp','?')} | "
            f"DD: {dd} | "
            f"SPY: {prices.get('SPY','?')} QQQ: {prices.get('QQQ','?')}"
        )


# ============================================================
# MAIN
# ============================================================
def main():
    print("=" * 60)
    print("  TRADE REAPER v3.0 — Dashboard + Bridge + Account Manager")
    print("=" * 60)
    print(f"  Bridge:    http://localhost:{BRIDGE_PORT}/levels")
    print(f"  JSON:      {JSON_FILE}")
    print(f"  CSV:       {CSV_FILE}")
    print(f"  Accounts:  {ACCOUNTS_FILE}")
    print(f"  Pressure:  {PRESSURE_FILE}")
    print("=" * 60)

    # Load pressure lines at startup
    load_pressure_lines()
    pl_count = sum(len(v) for inst in pressure_lines.values() for v in inst.values())
    if pl_count:
        print(f"  Loaded {pl_count} pressure line(s)")

    bridge_thread = threading.Thread(target=run_bridge, daemon=True)
    bridge_thread.start()

    if HAS_TK:
        try:
            dash = Dashboard()
            dash.run()
        except tk.TclError:
            print("[TradeReaper] Cannot open display, falling back to console mode")
            console_dashboard()
    else:
        console_dashboard()


if __name__ == "__main__":
    main()
