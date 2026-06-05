# M.GHAZI — Daily Expenses

A lightweight, single-file expense tracker that runs entirely in your browser. No build step, no server, no account — open the app and start logging where your money goes. All data is stored locally in your browser via `localStorage`.

**Live app:** https://hamashlo12359-tech.github.io/expenses-tracker/

## Features

- **Quick entry** — log an amount, category, and date in seconds.
- **At-a-glance stats** — totals for today, this week, this month, and all time.
- **Monthly budget** — set a cap and watch a progress bar with warning/over-budget alerts.
- **Category breakdown** — donut chart and per-category totals for the current month.
- **Daily spending chart** — bar chart of spending across the current month.
- **Filter & edit** — filter the list by category, edit or delete any entry.
- **Multi-currency** — USD, EUR, GBP, JPY, INR, SAR, AED.
- **CSV import / export** — back up your data or move it between devices.
- **Mobile friendly** — responsive layout, installable to your iPhone/iPad home screen.

## Usage

Open the [live app](https://hamashlo12359-tech.github.io/expenses-tracker/) in any browser — or open `index.html` locally — and start logging.

**On iPhone/iPad:** open the live link in **Safari**, tap the **Share** button, then **Add to Home Screen**. It installs with its own icon and launches fullscreen like a native app.

Because everything is stored in `localStorage`, your data stays on the device/browser you use. Use **Export** to download a CSV backup and **Import** to restore it on another device.

## Tech

Plain HTML, CSS, and vanilla JavaScript in a single file. Charts are hand-rolled inline SVG — no external dependencies or network requests.
