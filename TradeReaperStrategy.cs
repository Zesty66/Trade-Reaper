// ============================================================
// TradeReaperStrategy.cs — NinjaTrader 8 Strategy v3.0
// ============================================================
// Full auto-trading system with:
//   - Multi-account support (JSON config profiles)
//   - Zone trades (bull-to-bear, bear-to-bull)
//   - Arby's trades (sandwich zones)
//   - MHP trades (yellow line + yellow resilience)
//   - HP trades (blue line + blue resilience)
//   - Setup grading: EST, A+, A, B+
//   - Confluence decay auto-exit
//   - Trailing stop with breakeven + ratchet
//   - Daily profit/loss limits + stale data guard
//   - QuantVue-style chart overlay
// ============================================================

#region Using declarations
using System;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class TradeReaperStrategy : Strategy
    {
        // ==============================================
        // DATA STRUCTURES
        // ==============================================

        private struct Zone
        {
            public double Bottom;
            public double Top;
            public Zone(double b, double t) { Bottom = b; Top = t; }
        }

        private enum SetupGrade { None, B, A, APlus, EST }
        private enum SetupType { None, ZoneLong, ZoneShort, ArbysLong, ArbysShort, MHPLong, MHPShort, HPLong, HPShort, DDBandLong, DDBandShort }

        private class ActiveTrade
        {
            public SetupType Setup;
            public SetupGrade Grade;
            public double EntryPrice;
            public double Target;
            public double StopLoss;
            public double BreakevenThreshold;   // points to move to BE
            public double RatchetInterval;      // points between stop ratchets
            public double RatchetAmount;        // how much to move stop each ratchet
            public double HighestPnLPoints;     // track best PnL in points
            public bool StopAtBreakeven;        // has stop been moved to BE?
            public int RatchetCount;            // how many ratchets applied
            // Confluence snapshot at entry
            public double EntryDDRatio;
            public double EntryMainRes;
            public double EntryMHPRes;
            public double EntryHPRes;
            public bool UsesDD;
            public bool UsesMainRes;
            public bool UsesMHPRes;
            public bool UsesHPRes;
        }

        // ==============================================
        // LEVEL DATA
        // ==============================================
        private double levelHP, levelMHP, levelHG;
        private double levelOpen, levelClose;
        private double levelDDUpper, levelDDLower;
        private List<Zone> bullZones = new List<Zone>();
        private List<Zone> bearZones = new List<Zone>();

        // Pressure lines (hedging-derived exit levels — never enter, only exit)
        private List<double> yellowPressure = new List<double>();  // yellow pressure lines
        private List<double> redPressure = new List<double>();     // red pressure lines

        // Resilience: main = res, mhp = sp (yellow), hp = hp (blue)
        private double mainRes, mhpRes, hpRes;       // for current instrument
        private double spMainRes, spMHPRes, spHPRes;  // SP500 always
        private double nqMainRes, nqMHPRes, nqHPRes;  // NQ100 always
        private double ddRatio;
        private double livePrice;
        private string alertDirection = "-";

        // Trade tracking
        private ActiveTrade currentTrade;
        private DateTime lastLevelUpdate = DateTime.MinValue;
        private int updateFailCount;
        private bool levelsLoaded;
        private double sessionRealizedPnL;
        private double sessionStartCumProfit;  // CumProfit snapshot at session start
        private bool sessionStartCaptured;
        private double sessionHighPnL;
        private double maxDrawdown;
        private bool dailyLimitHit;
        private bool dailyProfitHit;

        // Two-strike rule: each level can only be traded twice per session
        // Key = level key like "BULL_24150.25", "BEAR_24300.00", "HP", "MHP", "DD_LOWER" etc.
        // Zone keys use the zone's price so they stay correct even if zones reorder on reload
        private Dictionary<string, int> levelHitCount = new Dictionary<string, int>();

        // Schedule (Eastern Time)
        private readonly TimeSpan marketOpen = new TimeSpan(9, 30, 0);
        private readonly TimeSpan marketClose = new TimeSpan(16, 0, 0);
        private bool okToTrade;
        private DateTime lastSessionDate = DateTime.MinValue;
        private int staleDataSeconds = 30;  // max age of CSV before we stop trading

        // Points per instrument
        private bool isNQ;
        private double bePts;          // 20 NQ, 5 ES
        private double ratchetIntPts;  // 10 NQ, 5 ES
        private double ratchetAmtPts;  // 2.5 NQ, 1 ES
        private double arbysMaxPts;    // 100 NQ, 25 ES

        // ==============================================
        // USER SETTINGS (visible in NinjaTrader strategy dialog)
        // ==============================================

        [NinjaScriptProperty]
        [TypeConverter(typeof(AccountProfileConverter))]
        [Display(Name = "Account Profile", Description = "Profile name from Trade Reaper's Account Manager", Order = 1, GroupName = "1. Setup")]
        public string AccountProfile { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Config File Path", Description = "Path to accounts_config.json", Order = 2, GroupName = "1. Setup")]
        public string ConfigFilePath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "CSV File Path", Description = "Path to levels_flat.csv", Order = 3, GroupName = "1. Setup")]
        public string CsvFilePath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = ">>> START BOT <<<", Description = "CHECK THIS TO ACTIVATE LIVE AUTO-TRADING", Order = 1, GroupName = "2. >>> START BOT <<<")]
        public bool EnableTrading { get; set; }

        // ==============================================
        // SETTINGS LOADED FROM PROFILE (not shown in NT dialog)
        // ==============================================
        private string InstrumentKey = "ES";
        private int RefreshSeconds = 2;
        private int Qty = 1;
        private double DailyLossLimit = 500;
        private double DailyProfitTarget = 0;
        private bool AllowEST = true;
        private bool AllowAPlus = true;
        private bool AllowA = true;
        private bool AllowBPlus = true;
        private bool AllowZoneTrades = true;
        private bool AllowArbysTrades = true;
        private bool AllowMHPTrades = true;
        private bool AllowHPTrades = true;
        private bool AllowDDBandTrades = true;
        private bool SkipFlip = true;
        private int StaleDataCutoff = 30;
        // Stop modes: "trailing" (BE + ratchet), "breakeven_plus" (move to small profit at threshold), "fixed" (never move)
        private string StopMode = "trailing";
        private double bePlusThresholdPts;   // 40 NQ, 10 ES — profit needed to trigger move
        private double bePlusProfitPts;      // 5 NQ, 1.25 ES — where stop moves to (above/below entry)

        // Track which profile loaded
        private string loadedProfileName = "";

        // ==============================================
        // LIFECYCLE
        // ==============================================

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Trade Reaper v3 — Multi-account auto-trading with Rocket Scooter levels";
                Name = "TradeReaperStrategy";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 60;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                IsFillLimitOnTouch = false;
                BarsRequiredToTrade = 5;
                IsInstantiatedOnEachOptimizationIteration = true;

                AccountProfile = "";
                // Auto-detect TradeReaper folder: Desktop, then Documents
                string trFolder = FindTradeReaperFolder();
                ConfigFilePath = Path.Combine(trFolder, "accounts_config.json");
                CsvFilePath = Path.Combine(trFolder, "levels_flat.csv");
                EnableTrading = false;
            }
            else if (State == State.Configure)
            {
                // Load account profile
                if (!string.IsNullOrEmpty(AccountProfile))
                {
                    LoadAccountProfile(AccountProfile);
                }
                else
                {
                    Print("TradeReaper: WARNING — No account profile set! Enter a profile name from Trade Reaper's Account Manager.");
                }

                // Detect instrument
                isNQ = InstrumentKey.ToUpper() == "NQ";
                bePts = isNQ ? 20 : 5;
                ratchetIntPts = isNQ ? 10 : 5;
                ratchetAmtPts = isNQ ? 2.5 : 1;
                arbysMaxPts = isNQ ? 100 : 25;
                bePlusThresholdPts = isNQ ? 40 : 10;   // profit needed to trigger breakeven_plus
                bePlusProfitPts = isNQ ? 5 : 1.25;     // stop moved to entry + this
            }
        }

        // ==============================================
        // ACCOUNT PROFILE LOADER
        // ==============================================

        private void LoadAccountProfile(string profileName)
        {
            try
            {
                if (!File.Exists(ConfigFilePath))
                {
                    Print("TradeReaper: Config file not found: " + ConfigFilePath);
                    return;
                }

                string json = File.ReadAllText(ConfigFilePath);

                // Simple JSON parser — no external libraries needed
                // Find the account block matching profileName
                string searchName = "\"name\":" + " " + "\"" + profileName + "\"";
                // More robust: search for "name": "profileName" with flexible whitespace
                int nameIdx = -1;
                string namePattern1 = "\"name\": \"" + profileName + "\"";
                string namePattern2 = "\"name\":\"" + profileName + "\"";

                nameIdx = json.IndexOf(namePattern1, StringComparison.OrdinalIgnoreCase);
                if (nameIdx < 0)
                    nameIdx = json.IndexOf(namePattern2, StringComparison.OrdinalIgnoreCase);

                if (nameIdx < 0)
                {
                    Print("TradeReaper: Account profile '" + profileName + "' not found in config");
                    return;
                }

                // Find the enclosing { } block
                int blockStart = json.LastIndexOf('{', nameIdx);
                int blockEnd = json.IndexOf('}', nameIdx);
                if (blockStart < 0 || blockEnd < 0)
                {
                    Print("TradeReaper: Malformed config block for " + profileName);
                    return;
                }

                string block = json.Substring(blockStart, blockEnd - blockStart + 1);

                // Parse individual fields
                InstrumentKey = ParseJsonString(block, "instrument") ?? InstrumentKey;
                Qty = ParseJsonInt(block, "qty", Qty);
                DailyLossLimit = ParseJsonDouble(block, "dailyLossLimit", DailyLossLimit);
                DailyProfitTarget = ParseJsonDouble(block, "dailyProfitTarget", DailyProfitTarget);
                StaleDataCutoff = ParseJsonInt(block, "staleDataCutoff", StaleDataCutoff);

                AllowEST = ParseJsonBool(block, "allowEST", AllowEST);
                AllowAPlus = ParseJsonBool(block, "allowAPlus", AllowAPlus);
                AllowA = ParseJsonBool(block, "allowA", AllowA);
                AllowBPlus = ParseJsonBool(block, "allowBPlus", AllowBPlus);
                AllowZoneTrades = ParseJsonBool(block, "allowZones", AllowZoneTrades);
                AllowArbysTrades = ParseJsonBool(block, "allowArbys", AllowArbysTrades);
                AllowMHPTrades = ParseJsonBool(block, "allowMHP", AllowMHPTrades);
                AllowHPTrades = ParseJsonBool(block, "allowHP", AllowHPTrades);
                AllowDDBandTrades = ParseJsonBool(block, "allowDDBand", AllowDDBandTrades);
                SkipFlip = ParseJsonBool(block, "skipFlip", SkipFlip);
                string parsedStopMode = ParseJsonString(block, "stopMode");
                if (!string.IsNullOrEmpty(parsedStopMode))
                    StopMode = parsedStopMode.ToLower();

                // Check if account is enabled
                bool enabled = ParseJsonBool(block, "enabled", true);
                if (!enabled)
                {
                    EnableTrading = false;
                    Print("TradeReaper: Account '" + profileName + "' is DISABLED in config");
                }

                loadedProfileName = profileName;
                double acctSize = ParseJsonDouble(block, "accountSize", 0);
                string notes = ParseJsonString(block, "notes") ?? "";

                Print(string.Format(
                    "TradeReaper: Loaded profile '{0}' | Size: ${1:N0} | {2} | Qty: {3} | DLL: ${4:N0} | Target: ${5:N0} | Stop: {6} | {7}",
                    profileName, acctSize, InstrumentKey, Qty, DailyLossLimit, DailyProfitTarget, StopMode, notes));
            }
            catch (Exception ex)
            {
                Print("TradeReaper: Error loading profile: " + ex.Message);
            }
        }

        // --- Simple JSON field parsers (no external dependencies) ---

        private string ParseJsonString(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            int quote1 = json.IndexOf('"', colon + 1);
            if (quote1 < 0) return null;
            int quote2 = json.IndexOf('"', quote1 + 1);
            if (quote2 < 0) return null;
            return json.Substring(quote1 + 1, quote2 - quote1 - 1);
        }

        private int ParseJsonInt(string json, string key, int fallback)
        {
            string raw = ExtractJsonRawValue(json, key);
            if (raw == null) return fallback;
            int val;
            return int.TryParse(raw, out val) ? val : fallback;
        }

        private double ParseJsonDouble(string json, string key, double fallback)
        {
            string raw = ExtractJsonRawValue(json, key);
            if (raw == null) return fallback;
            double val;
            return double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out val) ? val : fallback;
        }

        private bool ParseJsonBool(string json, string key, bool fallback)
        {
            string raw = ExtractJsonRawValue(json, key);
            if (raw == null) return fallback;
            if (raw.Trim().ToLower() == "true") return true;
            if (raw.Trim().ToLower() == "false") return false;
            return fallback;
        }

        private string ExtractJsonRawValue(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            // Find the value — skip whitespace
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
            if (start >= json.Length) return null;
            // If quoted string, return null (use ParseJsonString for those)
            if (json[start] == '"') return null;
            // Find end (comma, }, or newline)
            int end = start;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != '\n' && json[end] != '\r') end++;
            return json.Substring(start, end - start).Trim();
        }

        // ==============================================
        // MAIN LOOP
        // ==============================================

        protected override void OnBarUpdate()
        {
            // Need enough bars before accessing any series
            if (CurrentBar < BarsRequiredToTrade) return;

            // Always try to load levels so we can draw them (even on historical bars)
            if ((DateTime.Now - lastLevelUpdate).TotalSeconds >= RefreshSeconds)
                RefreshLevels();

            // Draw level lines on ALL bars (historical + real-time)
            if (levelsLoaded)
                DrawLevelLines();

            // Everything below is real-time only (trading, overlay, PnL)
            if (State != State.Realtime) return;

            // --- New session reset ---
            if (Time[0].Date != lastSessionDate)
            {
                lastSessionDate = Time[0].Date;
                sessionStartCaptured = false;  // will capture CumProfit on first fill
                sessionRealizedPnL = 0;
                sessionHighPnL = 0;
                maxDrawdown = 0;
                dailyLimitHit = false;
                dailyProfitHit = false;
                currentTrade = null;
                levelHitCount.Clear();  // Reset two-strike counters for new day
            }

            // --- Schedule: Mon-Fri 9:30 AM - 4:00 PM ET only ---
            TimeSpan now = Time[0].TimeOfDay;
            DayOfWeek dow = Time[0].DayOfWeek;
            bool isWeekday = dow != DayOfWeek.Saturday && dow != DayOfWeek.Sunday;
            bool isMarketHours = now >= marketOpen && now <= marketClose;
            bool wasOkToTrade = okToTrade;
            okToTrade = isWeekday && isMarketHours;

            // --- End-of-day flatten: if market just closed and we're still in a trade, get out ---
            if (wasOkToTrade && !okToTrade && Position.MarketPosition != MarketPosition.Flat)
            {
                Print("TradeReaper: MARKET CLOSED — flattening all positions");
                Flatten("MarketClose");
                currentTrade = null;
            }

            // --- Stale data guard: don't trade on old CSV data ---
            bool dataFresh = true;
            if (levelsLoaded && StaleDataCutoff > 0)
            {
                double dataAge = (DateTime.Now - lastLevelUpdate).TotalSeconds;
                if (dataAge > StaleDataCutoff)
                {
                    dataFresh = false;
                    // If we're in a trade and data goes stale, flatten for safety
                    if (currentTrade != null && Position.MarketPosition != MarketPosition.Flat)
                    {
                        Print("TradeReaper: CSV data is " + (int)dataAge + "s old — flattening for safety");
                        Flatten("StaleData");
                        currentTrade = null;
                    }
                }
            }

            // --- PnL tracking ---
            double unrealized = Position.MarketPosition != MarketPosition.Flat
                ? Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]) : 0;
            double totalPnL = sessionRealizedPnL + unrealized;

            if (totalPnL > sessionHighPnL) sessionHighPnL = totalPnL;
            maxDrawdown = Math.Max(maxDrawdown, sessionHighPnL - totalPnL);

            // --- Daily limits ---
            if (DailyLossLimit > 0 && totalPnL <= -DailyLossLimit)
            {
                if (!dailyLimitHit)
                    Print("TradeReaper: DAILY LOSS LIMIT HIT at $" + totalPnL.ToString("F2") + " — stopping trading");
                dailyLimitHit = true;
                if (Position.MarketPosition != MarketPosition.Flat) Flatten("DLL_Hit");
                currentTrade = null;
            }
            if (DailyProfitTarget > 0 && totalPnL >= DailyProfitTarget)
            {
                if (!dailyProfitHit)
                    Print("TradeReaper: DAILY PROFIT TARGET HIT at $" + totalPnL.ToString("F2") + " — stopping trading");
                dailyProfitHit = true;
                if (Position.MarketPosition != MarketPosition.Flat) Flatten("DailyProfit_Hit");
                currentTrade = null;
            }

            // --- Draw overlay + status (real-time only, levels drawn above) ---
            DrawOverlay(unrealized, totalPnL);
            DrawStatusBar(totalPnL, dataFresh);

            if (!EnableTrading || !okToTrade || !levelsLoaded || !dataFresh || dailyLimitHit || dailyProfitHit) return;

            double price = Close[0];

            // --- MANAGE EXISTING TRADE ---
            if (currentTrade != null && Position.MarketPosition != MarketPosition.Flat)
            {
                ManageActiveTrade(price);
                return; // Don't scan for new setups while in a trade
            }

            // If we're flat but currentTrade exists, clear it
            if (Position.MarketPosition == MarketPosition.Flat)
                currentTrade = null;

            // --- SCAN FOR NEW SETUPS ---
            ScanForSetups(price);
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition,
            string orderId, DateTime time)
        {
            if (execution.Order.OrderState == OrderState.Filled)
            {
                double cumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                // Capture the starting CumProfit on first fill of the day
                if (!sessionStartCaptured)
                {
                    sessionStartCumProfit = cumProfit;
                    sessionStartCaptured = true;
                }
                // Daily realized = today's fills only (total cumulative minus start-of-day snapshot)
                sessionRealizedPnL = cumProfit - sessionStartCumProfit;
            }
        }

        // ==============================================
        // SETUP SCANNER
        // ==============================================

        private void ScanForSetups(double price)
        {
            // Scan in priority order: EST > A+ > A > B+
            // Check each setup type

            // --- ZONE TRADES ---
            if (AllowZoneTrades)
            {
                double zoneMaxPts = isNQ ? 100 : 25; // Bull zone must be < 100pts NQ / 25pts ES

                // LONG: price at bottom of a bull zone, target = top of next bear zone
                for (int i = 0; i < bullZones.Count; i++)
                {
                    Zone bull = bullZones[i];
                    double bullSpan = bull.Top - bull.Bottom;

                    // Skip huge bull zones (> 100pts NQ / 25pts ES)
                    if (bullSpan > zoneMaxPts) continue;

                    // Entry at bottom of bull zone
                    string bullKey = "BULL_" + bull.Bottom.ToString("F2");
                    if (price >= bull.Bottom - 2 * TickSize && price <= bull.Bottom + 2 * TickSize
                        && !IsLevelStrikedOut(bullKey) && PassesPivotRule(bull.Bottom, true))
                    {
                        // Find the next bear zone above as target
                        Zone? targetBear = FindNextBearZoneAbove(bull.Top);
                        if (targetBear.HasValue)
                        {
                            double bearSpan = targetBear.Value.Top - targetBear.Value.Bottom;
                            // Skip huge bear zone targets (> 100pts NQ / 25pts ES)
                            if (bearSpan > zoneMaxPts) continue;

                            // Check if any pressure line or resistance is in the way — use as target instead
                            double rawTarget = targetBear.Value.Bottom;
                            double exitTarget = FindFirstExitAbove(price, rawTarget);

                            SetupGrade grade = GradeZoneTrade(true);
                            if (IsGradeAllowed(grade) && grade != SetupGrade.None)
                            {
                                EnterTrade(SetupType.ZoneLong, grade, price, exitTarget,
                                    bull.Bottom - bePts, true, false, false, mainRes > 10, bullKey);
                                return;
                            }
                        }
                    }
                }

                // SHORT: price at top of a bear zone, target = bottom of next bull zone
                for (int i = 0; i < bearZones.Count; i++)
                {
                    Zone bear = bearZones[i];
                    double bearSpan = bear.Top - bear.Bottom;

                    // Skip huge bear zones
                    if (bearSpan > zoneMaxPts) continue;

                    // Entry at top of bear zone
                    string bearKey = "BEAR_" + bear.Top.ToString("F2");
                    if (price >= bear.Top - 2 * TickSize && price <= bear.Top + 2 * TickSize
                        && !IsLevelStrikedOut(bearKey) && PassesPivotRule(bear.Top, false))
                    {
                        Zone? targetBull = FindNextBullZoneBelow(bear.Bottom);
                        if (targetBull.HasValue)
                        {
                            double bullSpan = targetBull.Value.Top - targetBull.Value.Bottom;
                            if (bullSpan > zoneMaxPts) continue;

                            double rawTarget = targetBull.Value.Top;
                            double exitTarget = FindFirstExitBelow(price, rawTarget);

                            SetupGrade grade = GradeZoneTrade(false);
                            if (IsGradeAllowed(grade) && grade != SetupGrade.None)
                            {
                                EnterTrade(SetupType.ZoneShort, grade, price, exitTarget,
                                    bear.Top + bePts, true, false, false, mainRes < -10, bearKey);
                                return;
                            }
                        }
                    }
                }
            }

            // --- ARBY'S TRADES ---
            if (AllowArbysTrades)
            {
                // Look for bear zone sandwiched between two bull zones (long)
                for (int i = 0; i < bullZones.Count - 1; i++)
                {
                    Zone bull1 = bullZones[i];
                    Zone bull2 = bullZones[i + 1];
                    // Is there a bear zone between them?
                    Zone? sandwich = FindBearZoneBetween(bull1.Top, bull2.Bottom);
                    if (sandwich.HasValue)
                    {
                        double totalDist = bull2.Bottom - bull1.Top;
                        if (totalDist <= arbysMaxPts && totalDist > 0)
                        {
                            // Entry at top of bull1
                            string arbysLongKey = "ARBYS_L_" + bull1.Top.ToString("F2");
                            if (price >= bull1.Top - 2 * TickSize && price <= bull1.Top + 2 * TickSize
                                && !IsLevelStrikedOut(arbysLongKey) && PassesPivotRule(bull1.Top, true))
                            {
                                SetupGrade grade = GradeZoneTrade(true); // Same grading as zone
                                if (IsGradeAllowed(grade) && grade != SetupGrade.None)
                                {
                                    EnterTrade(SetupType.ArbysLong, grade, price, bull2.Bottom,
                                        bull1.Bottom - 2 * TickSize, true, false, false, ddRatio > 0.60, arbysLongKey);
                                    return;
                                }
                            }
                        }
                    }
                }

                // Bull zone sandwiched between two bear zones (short)
                for (int i = 0; i < bearZones.Count - 1; i++)
                {
                    Zone bear1 = bearZones[i];
                    Zone bear2 = bearZones[i + 1];
                    Zone? sandwich = FindBullZoneBetween(bear1.Bottom, bear2.Top);
                    if (sandwich.HasValue)
                    {
                        double totalDist = bear1.Bottom - bear2.Top;
                        if (Math.Abs(totalDist) <= arbysMaxPts && totalDist != 0)
                        {
                            string arbysShortKey = "ARBYS_S_" + bear1.Bottom.ToString("F2");
                            if (price >= bear1.Bottom - 2 * TickSize && price <= bear1.Bottom + 2 * TickSize
                                && !IsLevelStrikedOut(arbysShortKey) && PassesPivotRule(bear1.Bottom, false))
                            {
                                SetupGrade grade = GradeZoneTrade(false);
                                if (IsGradeAllowed(grade) && grade != SetupGrade.None)
                                {
                                    EnterTrade(SetupType.ArbysShort, grade, price, bear2.Top,
                                        bear1.Top + 2 * TickSize, true, false, false, ddRatio < 0.40, arbysShortKey);
                                    return;
                                }
                            }
                        }
                    }
                }
            }

            // --- MHP TRADES ---
            if (AllowMHPTrades && levelMHP > 0 && !IsLevelStrikedOut("MHP"))
            {
                if (price >= levelMHP - 2 * TickSize && price <= levelMHP + 2 * TickSize)
                {
                    // LONG: mhpRes > 0 AND mainRes > 0
                    if (mhpRes > 0 && mainRes > 0 && PassesPivotRule(levelMHP, true))
                    {
                        SetupGrade grade = SetupGrade.APlus; // A+ by definition
                        if (ddRatio > 0.60) grade = SetupGrade.EST;
                        double target = FindNextLevelAbove(levelMHP);
                        if (IsGradeAllowed(grade))
                        {
                            EnterTrade(SetupType.MHPLong, grade, price, target,
                                levelMHP - bePts, false, true, false, ddRatio > 0.60, "MHP");
                            return;
                        }
                    }
                    // SHORT: mhpRes < 0 AND mainRes < 0
                    if (mhpRes < 0 && mainRes < 0 && PassesPivotRule(levelMHP, false))
                    {
                        SetupGrade grade = SetupGrade.APlus;
                        if (ddRatio < 0.40) grade = SetupGrade.EST;
                        double target = FindNextLevelBelow(levelMHP);
                        if (IsGradeAllowed(grade))
                        {
                            EnterTrade(SetupType.MHPShort, grade, price, target,
                                levelMHP + bePts, false, true, false, ddRatio < 0.40, "MHP");
                            return;
                        }
                    }
                }
            }

            // --- HP TRADES ---
            if (AllowHPTrades && levelHP > 0 && !IsLevelStrikedOut("HP"))
            {
                if (price >= levelHP - 2 * TickSize && price <= levelHP + 2 * TickSize)
                {
                    // LONG: hpRes > 0 AND mainRes > 0
                    if (hpRes > 0 && mainRes > 0 && PassesPivotRule(levelHP, true))
                    {
                        SetupGrade grade = SetupGrade.APlus;
                        if (ddRatio > 0.60) grade = SetupGrade.EST;
                        double target = FindNextLevelAbove(levelHP);
                        if (IsGradeAllowed(grade))
                        {
                            EnterTrade(SetupType.HPLong, grade, price, target,
                                levelHP - bePts, false, false, true, ddRatio > 0.60, "HP");
                            return;
                        }
                    }
                    // SHORT: hpRes < 0 AND mainRes < 0
                    if (hpRes < 0 && mainRes < 0 && PassesPivotRule(levelHP, false))
                    {
                        SetupGrade grade = SetupGrade.APlus;
                        if (ddRatio < 0.40) grade = SetupGrade.EST;
                        double target = FindNextLevelBelow(levelHP);
                        if (IsGradeAllowed(grade))
                        {
                            EnterTrade(SetupType.HPShort, grade, price, target,
                                levelHP + bePts, false, false, true, ddRatio < 0.40, "HP");
                            return;
                        }
                    }
                }
            }

            // --- DD BAND TRADES ---
            // LONG: price hits DD lower band + res positive + DD ratio > 0.60
            // SHORT: price hits DD upper band + res negative + DD ratio < 0.40
            // Both conditions must be met = A+ setup
            if (AllowDDBandTrades)
            {
                // LONG at DD lower band (no pivot rule needed — DD band IS the boundary)
                if (levelDDLower > 0 && !IsLevelStrikedOut("DD_LOWER")
                    && price >= levelDDLower - 2 * TickSize && price <= levelDDLower + 2 * TickSize)
                {
                    if (mainRes > 0 && ddRatio > 0.60)
                    {
                        SetupGrade grade = SetupGrade.APlus;
                        double target = FindNextLevelAbove(levelDDLower);
                        double exitTarget = FindFirstExitAbove(price, target);
                        if (IsGradeAllowed(grade))
                        {
                            EnterTrade(SetupType.DDBandLong, grade, price, exitTarget,
                                levelDDLower - bePts, true, false, false, true, "DD_LOWER");
                            return;
                        }
                    }
                }

                // SHORT at DD upper band (no pivot rule needed — DD band IS the boundary)
                if (levelDDUpper > 0 && !IsLevelStrikedOut("DD_UPPER")
                    && price >= levelDDUpper - 2 * TickSize && price <= levelDDUpper + 2 * TickSize)
                {
                    if (mainRes < 0 && ddRatio < 0.40)
                    {
                        SetupGrade grade = SetupGrade.APlus;
                        double target = FindNextLevelBelow(levelDDUpper);
                        double exitTarget = FindFirstExitBelow(price, target);
                        if (IsGradeAllowed(grade))
                        {
                            EnterTrade(SetupType.DDBandShort, grade, price, exitTarget,
                                levelDDUpper + bePts, true, false, false, true, "DD_UPPER");
                            return;
                        }
                    }
                }
            }
        }

        // ==============================================
        // TRADE MANAGEMENT
        // ==============================================

        private void ManageActiveTrade(double price)
        {
            if (currentTrade == null) return;

            bool isLong = Position.MarketPosition == MarketPosition.Long;
            double entryPx = currentTrade.EntryPrice;
            double pnlPoints = isLong ? (price - entryPx) : (entryPx - price);

            // Track best PnL
            if (pnlPoints > currentTrade.HighestPnLPoints)
                currentTrade.HighestPnLPoints = pnlPoints;

            // --- CONFLUENCE DECAY CHECK ---
            if (CheckConfluenceDecay())
            {
                Print("TradeReaper: Confluence decayed — exiting " + currentTrade.Setup);
                Flatten("ConfluenceDecay");
                currentTrade = null;
                return;
            }

            // --- TARGET HIT ---
            if (currentTrade.Target > 0)
            {
                if ((isLong && price >= currentTrade.Target) || (!isLong && price <= currentTrade.Target))
                {
                    Print("TradeReaper: Target hit at " + price);
                    Flatten("TargetHit");
                    currentTrade = null;
                    return;
                }
            }

            // --- STOP MANAGEMENT (mode-dependent) ---
            if (StopMode == "trailing")
            {
                // TRAILING: Move to breakeven after bePts profit, then ratchet
                if (!currentTrade.StopAtBreakeven && pnlPoints >= bePts)
                {
                    currentTrade.StopAtBreakeven = true;
                    currentTrade.StopLoss = entryPx;
                    currentTrade.RatchetCount = 0;
                    Print("TradeReaper: [Trailing] Stop moved to breakeven at " + entryPx);
                }
                if (currentTrade.StopAtBreakeven)
                {
                    double profitBeyondBE = pnlPoints - bePts;
                    int expectedRatchets = (int)(profitBeyondBE / ratchetIntPts);
                    if (expectedRatchets > currentTrade.RatchetCount)
                    {
                        currentTrade.RatchetCount = expectedRatchets;
                        double newStop = isLong
                            ? entryPx + (currentTrade.RatchetCount * ratchetAmtPts)
                            : entryPx - (currentTrade.RatchetCount * ratchetAmtPts);
                        currentTrade.StopLoss = newStop;
                        Print("TradeReaper: [Trailing] Stop ratcheted to " + newStop + " (ratchet #" + currentTrade.RatchetCount + ")");
                    }
                }
            }
            else if (StopMode == "breakeven_plus")
            {
                // BREAKEVEN PLUS: At 40pts NQ / 10pts ES profit, move stop to entry + 5pts NQ / 1.25pts ES
                if (!currentTrade.StopAtBreakeven && pnlPoints >= bePlusThresholdPts)
                {
                    currentTrade.StopAtBreakeven = true;
                    double newStop = isLong ? entryPx + bePlusProfitPts : entryPx - bePlusProfitPts;
                    currentTrade.StopLoss = newStop;
                    Print("TradeReaper: [BE+] Stop moved to " + newStop + " (+" + bePlusProfitPts + "pts profit lock)");
                }
                // No further ratcheting — stop stays where it was moved
            }
            // else StopMode == "fixed": stop never moves from initial level

            // --- STOP HIT ---
            if (currentTrade.StopLoss > 0)
            {
                if ((isLong && price <= currentTrade.StopLoss) || (!isLong && price >= currentTrade.StopLoss))
                {
                    Print("TradeReaper: Stop hit at " + price);
                    Flatten("StopHit");
                    currentTrade = null;
                    return;
                }
            }
        }

        private bool CheckConfluenceDecay()
        {
            if (currentTrade == null) return false;

            bool isLong = currentTrade.Setup.ToString().Contains("Long");

            // DD ratio confluence
            if (currentTrade.UsesDD)
            {
                if (isLong && ddRatio <= 0.40) return true;   // Was > 0.60, now flipped
                if (!isLong && ddRatio >= 0.60) return true;  // Was < 0.40, now flipped
            }

            // Main resilience confluence
            if (currentTrade.UsesMainRes)
            {
                if (isLong && mainRes < 0) return true;   // Was positive, flipped negative
                if (!isLong && mainRes > 0) return true;   // Was negative, flipped positive
            }

            // MHP resilience confluence
            if (currentTrade.UsesMHPRes)
            {
                if (isLong && mhpRes < 0) return true;
                if (!isLong && mhpRes > 0) return true;
            }

            // HP resilience confluence
            if (currentTrade.UsesHPRes)
            {
                if (isLong && hpRes < 0) return true;
                if (!isLong && hpRes > 0) return true;
            }

            return false;
        }

        // ==============================================
        // GRADING
        // ==============================================

        private SetupGrade GradeZoneTrade(bool isLong)
        {
            // Strict thresholds: DD must be above .75 (long) or below .25 (short)
            // Resilience must be above +10 (long) or below -10 (short)
            bool ddStrong = isLong ? ddRatio > 0.75 : ddRatio < 0.25;
            bool resStrong = isLong ? mainRes > 10 : mainRes < -10;

            // EST: both DD and resilience strongly confirm
            if (ddStrong && resStrong) return SetupGrade.EST;
            // A+: DD confirms strongly, resilience at least in the right direction
            bool resOk = isLong ? mainRes > 0 : mainRes < 0;
            if (ddStrong && resOk) return SetupGrade.APlus;
            // A: DD is decent (.60/.40) and resilience confirms direction
            bool ddOk = isLong ? ddRatio > 0.60 : ddRatio < 0.40;
            if (ddOk && resStrong) return SetupGrade.A;
            // B+: DD at least decent
            if (ddOk && resOk) return SetupGrade.B;
            // No trade without at least DD + res confirmation
            return SetupGrade.None;
        }

        private bool IsGradeAllowed(SetupGrade grade)
        {
            switch (grade)
            {
                case SetupGrade.EST: return AllowEST;
                case SetupGrade.APlus: return AllowAPlus;
                case SetupGrade.A: return AllowA;
                case SetupGrade.B: return AllowBPlus;
                default: return false;
            }
        }

        // ==============================================
        // AUTO-DETECT TRADEREAPER FOLDER
        // ==============================================

        private string FindTradeReaperFolder()
        {
            // Check common locations for TradeReaper folder
            string[] searchPaths = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "TradeReaper"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "TradeReaper"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TradeReaper"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Desktop", "TradeReaper"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "TradeReaper"),
            };

            foreach (string p in searchPaths)
            {
                if (Directory.Exists(p))
                {
                    Print("TradeReaper: Found folder at " + p);
                    return p;
                }
            }

            // Fallback: Desktop\TradeReaper (will show clear error if missing)
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "TradeReaper");
            Print("TradeReaper: WARNING — Could not find TradeReaper folder. Expected at: " + fallback);
            return fallback;
        }

        // ==============================================
        // TWO-STRIKE RULE
        // ==============================================

        private bool IsLevelStrikedOut(string levelKey)
        {
            if (levelHitCount.ContainsKey(levelKey))
                return levelHitCount[levelKey] >= 2;
            return false;
        }

        private void RecordLevelHit(string levelKey)
        {
            if (levelHitCount.ContainsKey(levelKey))
                levelHitCount[levelKey]++;
            else
                levelHitCount[levelKey] = 1;
        }

        // ==============================================
        // OUTSIDE DD BAND PIVOT RULE
        // ==============================================
        // When price is outside DD bands, only trade if price is
        // FALLING to a long level (retest from above) or
        // RISING to a short level (retest from below).
        // We check if the bar 2 bars ago was on the opposite side of the level.

        private bool IsOutsideDDBands(double price)
        {
            if (levelDDUpper <= 0 || levelDDLower <= 0) return false;
            return price > levelDDUpper || price < levelDDLower;
        }

        private bool PassesPivotRule(double levelPrice, bool isLong)
        {
            // If inside DD bands, no pivot rule needed — all setups are valid
            double price = Close[0];
            if (!IsOutsideDDBands(price)) return true;

            // Need at least 3 bars to check approach direction
            if (CurrentBar < 3) return true;

            if (isLong)
            {
                // For a long outside DD bands: price must be FALLING to the level
                // Check that a recent bar was ABOVE the level (price came down to it)
                return High[1] > levelPrice + 2 * TickSize || High[2] > levelPrice + 2 * TickSize;
            }
            else
            {
                // For a short outside DD bands: price must be RISING to the level
                // Check that a recent bar was BELOW the level (price came up to it)
                return Low[1] < levelPrice - 2 * TickSize || Low[2] < levelPrice - 2 * TickSize;
            }
        }

        // ==============================================
        // ENTRY
        // ==============================================

        private void EnterTrade(SetupType setup, SetupGrade grade, double price, double target,
            double stop, bool usesDD, bool usesMHP, bool usesHP, bool usesMainRes_flag,
            string levelKey = "")
        {
            // Record the level hit for two-strike tracking
            if (!string.IsNullOrEmpty(levelKey))
                RecordLevelHit(levelKey);

            bool isLong = setup.ToString().Contains("Long");

            currentTrade = new ActiveTrade
            {
                Setup = setup,
                Grade = grade,
                EntryPrice = price,
                Target = target,
                StopLoss = stop,
                BreakevenThreshold = bePts,
                RatchetInterval = ratchetIntPts,
                RatchetAmount = ratchetAmtPts,
                HighestPnLPoints = 0,
                StopAtBreakeven = false,
                RatchetCount = 0,
                EntryDDRatio = ddRatio,
                EntryMainRes = mainRes,
                EntryMHPRes = mhpRes,
                EntryHPRes = hpRes,
                UsesDD = usesDD,
                UsesMainRes = usesMainRes_flag || (setup == SetupType.MHPLong || setup == SetupType.MHPShort
                    || setup == SetupType.HPLong || setup == SetupType.HPShort),
                UsesMHPRes = usesMHP,
                UsesHPRes = usesHP
            };

            string signal = setup.ToString() + "_" + grade.ToString();
            Print(string.Format("TradeReaper: {0} {1} @ {2} | Target: {3} | Stop: {4} | DD: {5:F2} | MainRes: {6:F1}",
                grade, setup, price, target, stop, ddRatio, mainRes));

            if (isLong)
                EnterLong(Qty, signal);
            else
                EnterShort(Qty, signal);
        }

        private void Flatten(string reason)
        {
            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong(reason);
            else if (Position.MarketPosition == MarketPosition.Short)
                ExitShort(reason);
        }

        // ==============================================
        // LEVEL HELPERS
        // ==============================================

        private Zone? FindNextBearZoneAbove(double price)
        {
            foreach (var z in bearZones)
                if (z.Bottom > price) return z;
            return null;
        }

        private Zone? FindNextBullZoneBelow(double price)
        {
            for (int i = bullZones.Count - 1; i >= 0; i--)
                if (bullZones[i].Top < price) return bullZones[i];
            return null;
        }

        private Zone? FindBearZoneBetween(double low, double high)
        {
            foreach (var z in bearZones)
                if (z.Bottom >= low && z.Top <= high) return z;
            return null;
        }

        private Zone? FindBullZoneBetween(double low, double high)
        {
            foreach (var z in bullZones)
                if (z.Bottom >= low && z.Top <= high) return z;
            return null;
        }

        private double FindNextLevelAbove(double price)
        {
            var candidates = new List<double>();
            if (levelDDUpper > price) candidates.Add(levelDDUpper);
            if (levelClose > price) candidates.Add(levelClose);
            if (levelOpen > price) candidates.Add(levelOpen);
            if (levelHG > price) candidates.Add(levelHG);
            if (levelHP > price) candidates.Add(levelHP);
            if (levelMHP > price) candidates.Add(levelMHP);
            foreach (var z in bearZones) if (z.Bottom > price) candidates.Add(z.Bottom);
            foreach (var z in bullZones) if (z.Top > price) candidates.Add(z.Top);
            // Pressure lines act as resistance above
            foreach (double p in yellowPressure) if (p > price) candidates.Add(p);
            foreach (double p in redPressure) if (p > price) candidates.Add(p);

            return candidates.Count > 0 ? candidates.Min() : price + (isNQ ? 100 : 25);
        }

        private double FindNextLevelBelow(double price)
        {
            var candidates = new List<double>();
            if (levelDDLower > 0 && levelDDLower < price) candidates.Add(levelDDLower);
            if (levelClose > 0 && levelClose < price) candidates.Add(levelClose);
            if (levelOpen > 0 && levelOpen < price) candidates.Add(levelOpen);
            if (levelHG > 0 && levelHG < price) candidates.Add(levelHG);
            if (levelHP > 0 && levelHP < price) candidates.Add(levelHP);
            if (levelMHP > 0 && levelMHP < price) candidates.Add(levelMHP);
            foreach (var z in bullZones) if (z.Top < price) candidates.Add(z.Top);
            foreach (var z in bearZones) if (z.Bottom < price) candidates.Add(z.Bottom);
            // Pressure lines act as support below
            foreach (double p in yellowPressure) if (p < price) candidates.Add(p);
            foreach (double p in redPressure) if (p < price) candidates.Add(p);

            return candidates.Count > 0 ? candidates.Max() : price - (isNQ ? 100 : 25);
        }

        /// <summary>
        /// For longs: find the first exit-level (pressure line, resistance) between entry and raw target.
        /// If a pressure line or level is in the way, use that as the target instead.
        /// </summary>
        private double FindFirstExitAbove(double entry, double maxTarget)
        {
            var blockers = new List<double>();
            // Pressure lines push opposite direction = exit for longs
            foreach (double p in yellowPressure) if (p > entry && p < maxTarget) blockers.Add(p);
            foreach (double p in redPressure) if (p > entry && p < maxTarget) blockers.Add(p);
            // Key resistance levels in the way
            if (levelDDUpper > entry && levelDDUpper < maxTarget) blockers.Add(levelDDUpper);
            if (levelHP > entry && levelHP < maxTarget) blockers.Add(levelHP);
            if (levelMHP > entry && levelMHP < maxTarget) blockers.Add(levelMHP);
            if (levelClose > entry && levelClose < maxTarget) blockers.Add(levelClose);

            return blockers.Count > 0 ? blockers.Min() : maxTarget;
        }

        /// <summary>
        /// For shorts: find the first exit-level (pressure line, support) between entry and raw target.
        /// </summary>
        private double FindFirstExitBelow(double entry, double minTarget)
        {
            var blockers = new List<double>();
            foreach (double p in yellowPressure) if (p < entry && p > minTarget) blockers.Add(p);
            foreach (double p in redPressure) if (p < entry && p > minTarget) blockers.Add(p);
            if (levelDDLower > 0 && levelDDLower < entry && levelDDLower > minTarget) blockers.Add(levelDDLower);
            if (levelHP > 0 && levelHP < entry && levelHP > minTarget) blockers.Add(levelHP);
            if (levelMHP > 0 && levelMHP < entry && levelMHP > minTarget) blockers.Add(levelMHP);
            if (levelClose > 0 && levelClose < entry && levelClose > minTarget) blockers.Add(levelClose);

            return blockers.Count > 0 ? blockers.Max() : minTarget;
        }

        // ==============================================
        // CHART DRAWING
        // ==============================================

        private void DrawOverlay(double unrealized, double totalPnL)
        {
            string tradeInfo = currentTrade != null
                ? string.Format("{0} {1} | Stop: {2:F2} | Tgt: {3:F2}",
                    currentTrade.Grade, currentTrade.Setup, currentTrade.StopLoss, currentTrade.Target)
                : "No active trade";

            string profileTag = string.IsNullOrEmpty(loadedProfileName) ? "Manual" : loadedProfileName;
            string skipTag = SkipFlip ? " SkipFlip" : "";
            string overlay = string.Format(
                "TradeReaper V3.0 [{0}]{1}\n" +
                "UnRealized PnL: {2:C2}\n" +
                "Realized PnL: {3:C2}\n\n" +
                "SP Res: {4:F1} | {5:F1} | {6:F1}\n" +
                "NQ Res: {7:F1} | {8:F1} | {9:F1}\n" +
                "DD: {10:F2}\n\n" +
                "{11}",
                profileTag, skipTag,
                unrealized, sessionRealizedPnL,
                spMainRes, spMHPRes, spHPRes,
                nqMainRes, nqMHPRes, nqHPRes,
                ddRatio, tradeInfo);

            Draw.TextFixed(this, "Overlay", overlay, TextPosition.TopLeft,
                Brushes.Cyan, new SimpleFont("Consolas", 11),
                Brushes.Transparent, Brushes.Transparent, 0);
        }

        private void DrawStatusBar(double totalPnL, bool dataFresh)
        {
            bool canTrade = EnableTrading && okToTrade && !dailyLimitHit && !dailyProfitHit && levelsLoaded && dataFresh;
            double ageSeconds = levelsLoaded ? (DateTime.Now - lastLevelUpdate).TotalSeconds : -1;
            string freshStr = ageSeconds < 0 ? "N/A" : (dataFresh ? string.Format("OK ({0:F0}s)", ageSeconds) : string.Format("STALE ({0:F0}s)", ageSeconds));
            string status = string.Format(
                "PnL: {0:C2} | High: {1:C2} | DD: {2:C2} | DLL? {3} | Profit? {4} | Data: {5} | Trade: {6}",
                totalPnL, sessionHighPnL, maxDrawdown,
                dailyLimitHit, dailyProfitHit, freshStr, canTrade);

            Draw.TextFixed(this, "StatusBar", status, TextPosition.BottomLeft,
                totalPnL >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed,
                new SimpleFont("Consolas", 10),
                Brushes.Black, Brushes.Black, 80);
        }

        private void DrawLevelLines()
        {
            // --- Zone rectangles: span from 9:30 AM to 4:00 PM today ---
            DateTime today = Time[0].Date;
            DateTime zoneStart = today.Add(marketOpen);   // 9:30 AM
            DateTime zoneEnd   = today.Add(marketClose);  // 4:00 PM

            // --- Labeled horizontal lines ---
            if (levelHP > 0)
            {
                Draw.HorizontalLine(this, "HP", levelHP, Brushes.DeepSkyBlue, DashStyleHelper.Dash, 2);
                Draw.Text(this, "HP_L", false, "HP " + levelHP.ToString("F2"), Time[0], levelHP, 10,
                    Brushes.DeepSkyBlue, new SimpleFont("Consolas", 9), System.Windows.TextAlignment.Right,
                    Brushes.Transparent, Brushes.Transparent, 0);
            }
            if (levelMHP > 0)
            {
                Draw.HorizontalLine(this, "MHP", levelMHP, Brushes.Gold, DashStyleHelper.Dash, 2);
                Draw.Text(this, "MHP_L", false, "MHP " + levelMHP.ToString("F2"), Time[0], levelMHP, 10,
                    Brushes.Gold, new SimpleFont("Consolas", 9), System.Windows.TextAlignment.Right,
                    Brushes.Transparent, Brushes.Transparent, 0);
            }
            if (levelHG > 0)
            {
                Draw.HorizontalLine(this, "HG", levelHG, Brushes.Yellow, DashStyleHelper.Dash, 1);
                Draw.Text(this, "HG_L", false, "HG " + levelHG.ToString("F2"), Time[0], levelHG, 10,
                    Brushes.Yellow, new SimpleFont("Consolas", 9), System.Windows.TextAlignment.Right,
                    Brushes.Transparent, Brushes.Transparent, 0);
            }
            if (levelOpen > 0)
            {
                Draw.HorizontalLine(this, "Open", levelOpen, Brushes.White, DashStyleHelper.Dot, 1);
                Draw.Text(this, "Open_L", false, "OPEN " + levelOpen.ToString("F2"), Time[0], levelOpen, 10,
                    Brushes.White, new SimpleFont("Consolas", 9), System.Windows.TextAlignment.Right,
                    Brushes.Transparent, Brushes.Transparent, 0);
            }
            if (levelClose > 0)
            {
                Draw.HorizontalLine(this, "Close", levelClose, Brushes.Gray, DashStyleHelper.Dot, 1);
                Draw.Text(this, "Close_L", false, "CLOSE " + levelClose.ToString("F2"), Time[0], levelClose, 10,
                    Brushes.Gray, new SimpleFont("Consolas", 9), System.Windows.TextAlignment.Right,
                    Brushes.Transparent, Brushes.Transparent, 0);
            }
            if (levelDDUpper > 0)
            {
                Draw.HorizontalLine(this, "DD_Hi", levelDDUpper, Brushes.Orange, DashStyleHelper.Dash, 1);
                Draw.Text(this, "DDHi_L", false, "DD HIGH " + levelDDUpper.ToString("F2"), Time[0], levelDDUpper, 10,
                    Brushes.Orange, new SimpleFont("Consolas", 9), System.Windows.TextAlignment.Right,
                    Brushes.Transparent, Brushes.Transparent, 0);
            }
            if (levelDDLower > 0)
            {
                Draw.HorizontalLine(this, "DD_Lo", levelDDLower, Brushes.Orange, DashStyleHelper.Dash, 1);
                Draw.Text(this, "DDLo_L", false, "DD LOW " + levelDDLower.ToString("F2"), Time[0], levelDDLower, -10,
                    Brushes.Orange, new SimpleFont("Consolas", 9), System.Windows.TextAlignment.Right,
                    Brushes.Transparent, Brushes.Transparent, 0);
            }

            // --- Bull zones: green shading from 9:30 to 4:00 ---
            for (int i = 0; i < bullZones.Count && i < 10; i++)
            {
                Draw.Rectangle(this, "Bull_" + i, false, zoneStart, bullZones[i].Top, zoneEnd, bullZones[i].Bottom,
                    Brushes.Transparent, Brushes.DarkGreen, 30);
                // Label at the right edge
                Draw.Text(this, "BullLbl_" + i, false,
                    "BULL " + bullZones[i].Bottom.ToString("F2") + "-" + bullZones[i].Top.ToString("F2"),
                    Time[0], (bullZones[i].Top + bullZones[i].Bottom) / 2, 0,
                    Brushes.Lime, new SimpleFont("Consolas", 8), System.Windows.TextAlignment.Right,
                    Brushes.Transparent, Brushes.Transparent, 0);
            }

            // --- Bear zones: red shading from 9:30 to 4:00 ---
            for (int i = 0; i < bearZones.Count && i < 10; i++)
            {
                Draw.Rectangle(this, "Bear_" + i, false, zoneStart, bearZones[i].Top, zoneEnd, bearZones[i].Bottom,
                    Brushes.Transparent, Brushes.DarkRed, 30);
                Draw.Text(this, "BearLbl_" + i, false,
                    "BEAR " + bearZones[i].Bottom.ToString("F2") + "-" + bearZones[i].Top.ToString("F2"),
                    Time[0], (bearZones[i].Top + bearZones[i].Bottom) / 2, 0,
                    Brushes.Tomato, new SimpleFont("Consolas", 8), System.Windows.TextAlignment.Right,
                    Brushes.Transparent, Brushes.Transparent, 0);
            }

            // --- Yellow pressure lines (dashed yellow, exit-only) ---
            for (int i = 0; i < yellowPressure.Count && i < 10; i++)
            {
                Draw.HorizontalLine(this, "YP_" + i, yellowPressure[i], Brushes.Yellow, DashStyleHelper.DashDot, 2);
                Draw.Text(this, "YPLbl_" + i, false,
                    "Y-PRESS " + yellowPressure[i].ToString("F2"),
                    Time[0], yellowPressure[i], 10,
                    Brushes.Yellow, new SimpleFont("Consolas", 8), System.Windows.TextAlignment.Right,
                    Brushes.Transparent, Brushes.Transparent, 0);
            }

            // --- Red pressure lines (dashed red, exit-only) ---
            for (int i = 0; i < redPressure.Count && i < 10; i++)
            {
                Draw.HorizontalLine(this, "RP_" + i, redPressure[i], Brushes.Red, DashStyleHelper.DashDot, 2);
                Draw.Text(this, "RPLbl_" + i, false,
                    "R-PRESS " + redPressure[i].ToString("F2"),
                    Time[0], redPressure[i], 10,
                    Brushes.Red, new SimpleFont("Consolas", 8), System.Windows.TextAlignment.Right,
                    Brushes.Transparent, Brushes.Transparent, 0);
            }
        }

        // ==============================================
        // DATA LOADING
        // ==============================================

        private void RefreshLevels()
        {
            try
            {
                if (!File.Exists(CsvFilePath)) return;

                string[] lines = File.ReadAllLines(CsvFilePath);
                string inst = InstrumentKey.ToUpper();
                string resKey = inst == "ES" ? "SP500" : "NQ100";

                var newBull = new List<Zone>();
                var newBear = new List<Zone>();
                var newYellowPressure = new List<double>();
                var newRedPressure = new List<double>();

                // Temp storage for zone pairs (index → bottom/top)
                var bullBots = new Dictionary<int, double>();
                var bullTops = new Dictionary<int, double>();
                var bearBots = new Dictionary<int, double>();
                var bearTops = new Dictionary<int, double>();

                foreach (string line in lines)
                {
                    if (line.StartsWith("instrument")) continue;
                    string[] p = line.Split(',');
                    if (p.Length < 3) continue;

                    string i = p[0].Trim().ToUpper();
                    string lt = p[1].Trim().ToUpper();
                    double val;
                    if (!double.TryParse(p[2].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out val)) continue;
                    int idx = 0;
                    if (p.Length > 3 && !string.IsNullOrWhiteSpace(p[3]))
                        int.TryParse(p[3].Trim(), out idx);

                    if (i == inst)
                    {
                        switch (lt)
                        {
                            case "HP": levelHP = val; break;
                            case "MHP": levelMHP = val; break;
                            case "HG": levelHG = val; break;
                            case "OPEN": levelOpen = val; break;
                            case "CLOSE": levelClose = val; break;
                            case "DD_UPPER": levelDDUpper = val; break;
                            case "DD_LOWER": levelDDLower = val; break;
                            case "BULL_ZONE_BOT": bullBots[idx] = val; break;
                            case "BULL_ZONE_TOP": bullTops[idx] = val; break;
                            case "BEAR_ZONE_BOT": bearBots[idx] = val; break;
                            case "BEAR_ZONE_TOP": bearTops[idx] = val; break;
                            case "YELLOW_PRESSURE": newYellowPressure.Add(val); break;
                            case "RED_PRESSURE": newRedPressure.Add(val); break;
                        }
                    }

                    // SP500 resilience
                    if (i == "SP500")
                    {
                        switch (lt)
                        {
                            case "RES": spMainRes = val; break;
                            case "RES_SP": spMHPRes = val; break;  // yellow = MHP
                            case "RES_HP": spHPRes = val; break;   // blue = HP
                        }
                    }
                    // NQ100 resilience
                    if (i == "NQ100")
                    {
                        switch (lt)
                        {
                            case "RES": nqMainRes = val; break;
                            case "RES_SP": nqMHPRes = val; break;
                            case "RES_HP": nqHPRes = val; break;
                        }
                    }

                    if (i == "MARKET" && lt == "DD_RATIO") ddRatio = val;
                }

                // Set current instrument's resilience
                if (inst == "ES") { mainRes = spMainRes; mhpRes = spMHPRes; hpRes = spHPRes; }
                else { mainRes = nqMainRes; mhpRes = nqMHPRes; hpRes = nqHPRes; }

                // Build zone lists from paired bot/top values
                foreach (int k in bullBots.Keys)
                {
                    double bot = bullBots[k];
                    double top = bullTops.ContainsKey(k) ? bullTops[k] : bot;
                    newBull.Add(new Zone(bot, top));
                }
                foreach (int k in bearBots.Keys)
                {
                    double bot = bearBots[k];
                    double top = bearTops.ContainsKey(k) ? bearTops[k] : bot;
                    newBear.Add(new Zone(bot, top));
                }

                newBull.Sort((a, b) => a.Bottom.CompareTo(b.Bottom));
                newBear.Sort((a, b) => a.Bottom.CompareTo(b.Bottom));
                bullZones = newBull;
                bearZones = newBear;
                newYellowPressure.Sort();
                newRedPressure.Sort();
                yellowPressure = newYellowPressure;
                redPressure = newRedPressure;

                lastLevelUpdate = DateTime.Now;
                updateFailCount = 0;
                levelsLoaded = true;
            }
            catch (Exception ex)
            {
                if (updateFailCount++ % 20 == 0)
                    Print("TradeReaper: " + ex.Message);
            }
        }
    }

    // ==============================================
    // DROPDOWN CONVERTER — reads profile names from accounts_config.json
    // ==============================================
    public class AccountProfileConverter : TypeConverter
    {
        private static readonly string configPath =
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "TradeReaper", "accounts_config.json");

        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return false; } // allow typing too

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            var names = new List<string>();
            try
            {
                // Also check OneDrive Desktop path
                string path1 = configPath;
                string path2 = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    @"OneDrive\Desktop\TradeReaper\accounts_config.json");

                string usePath = null;
                if (File.Exists(path1)) usePath = path1;
                else if (File.Exists(path2)) usePath = path2;

                if (usePath != null)
                {
                    string json = File.ReadAllText(usePath);
                    // Simple extraction: find all "name": "value" pairs
                    int searchFrom = 0;
                    while (true)
                    {
                        int idx = json.IndexOf("\"name\"", searchFrom, StringComparison.OrdinalIgnoreCase);
                        if (idx < 0) break;
                        int colon = json.IndexOf(':', idx + 6);
                        if (colon < 0) break;
                        int q1 = json.IndexOf('"', colon + 1);
                        if (q1 < 0) break;
                        int q2 = json.IndexOf('"', q1 + 1);
                        if (q2 < 0) break;
                        string name = json.Substring(q1 + 1, q2 - q1 - 1);
                        if (!string.IsNullOrWhiteSpace(name))
                            names.Add(name);
                        searchFrom = q2 + 1;
                    }
                }
            }
            catch { }

            if (names.Count == 0)
                names.Add("(no profiles found)");

            return new StandardValuesCollection(names);
        }
    }
}
