# Trade Reaper v3.0

Auto-trading system for NinjaTrader 8 that scrapes levels from Rocket Scooter, bridges data via Python, and executes trades automatically.

## Components

- **trade_reaper.py** — Dashboard + Bridge Server (tkinter GUI, HTTP bridge on port 8777)
- **rocket_scraper.js** — Browser scraper for Rocket Scooter (paste into F12 console)
- **TradeReaperStrategy.cs** — NinjaTrader 8 strategy (auto-trades zones, Arby's, MHP, HP, DD bands)
- **accounts_config.json** — Account profiles (edit in the Account Manager tab)

## Quick Start

See **QUICKSTART.txt** for full setup instructions.

1. Put this folder on your Desktop as `TradeReaper`
2. Run `python trade_reaper.py` to start the dashboard
3. Open Rocket Scooter in browser → F12 → paste `rocket_scraper.js`
4. Install strategy in NinjaTrader 8 → set Account Profile name → enable trading

## Features

- Multi-account support with per-account settings
- Zone, Arby's, MHP, HP, and DD Band trade setups
- Setup grading: EST, A+, A, B+
- Two-strike rule (each level max 2 trades per session)
- Outside-DD-band pivot rule
- Three stop modes: trailing, breakeven_plus, fixed
- Confluence decay auto-exit
- Daily profit/loss limits + stale data guard
- Auto-detects TradeReaper folder on any machine

## Disclaimer

This is a tool, not financial advice. Always paper trade first. Use at your own risk.
