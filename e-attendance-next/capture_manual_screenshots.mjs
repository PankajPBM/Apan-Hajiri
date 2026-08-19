import fs from 'fs';
import path from 'path';
import puppeteer from 'puppeteer-core';

const CHROME_PATH = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
const EDGE_PATH = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const BROWSER_PATH = fs.existsSync(CHROME_PATH) ? CHROME_PATH : EDGE_PATH;
const BASE_URL = "http://127.0.0.1:8073";
const API_BASE = "http://127.0.0.1:8081";
const OUTPUT_DIR = path.resolve(process.cwd(), "..", "manual_assets");

if (!fs.existsSync(OUTPUT_DIR)) {
  fs.mkdirSync(OUTPUT_DIR, { recursive: true });
}

const ADMIN_SESSION = {
  userId: "default-admin",
  username: "admin",
  fullName: "System Administrator",
  role: "Admin",
  isActive: true,
  featureAccess: {
    dashboard: true,
    departments: true,
    devices: true,
    employees: true,
    timetable: true,
    shift: true,
    assign: true,
    transactions: true,
    leaves: true,
    reports: true,
    logs: true,
    settings: true
  },
  loginAt: new Date().toISOString()
};

const routes = [
  { name: "01_login", path: "/login", needAuth: false },
  { name: "02_dashboard", path: "/", needAuth: true },
  { name: "03_setup_areas", path: "/setup?section=areas", needAuth: true },
  { name: "04_devices", path: "/devices", needAuth: true },
  { name: "05_employees", path: "/employees", needAuth: true },
  { name: "06_shift_break", path: "/shifts?tab=break", needAuth: true },
  { name: "07_shift_timetable", path: "/shifts?tab=timetable", needAuth: true },
  { name: "08_shift_setup", path: "/shifts?tab=setup", needAuth: true },
  { name: "09_schedule_assignment", path: "/assign", needAuth: true },
  { name: "10_schedule_groups", path: "/assign?section=groups", needAuth: true },
  { name: "11_schedule_preview", path: "/assign?section=preview", needAuth: true },
  { name: "12_access_time_period", path: "/access-control?section=time-period", needAuth: true },
  { name: "13_access_groups", path: "/access-control?section=groups", needAuth: true },
  { name: "14_access_combination", path: "/access-control?section=combination", needAuth: true },
  { name: "15_access_privilege", path: "/access-control?section=privilege", needAuth: true },
  { name: "16_transactions", path: "/attendance", needAuth: true },
  { name: "17_leaves_holiday", path: "/leaves", needAuth: true },
  { name: "18_reports", path: "/reports", needAuth: true },
  { name: "19_logs", path: "/logs", needAuth: true },
  { name: "20_settings", path: "/settings", needAuth: true },
  { name: "21_apply_leave", path: "/apply-leave", needAuth: true },
  { name: "22_check_in", path: "/check-in", needAuth: true },
];

async function run() {
  console.log("Launching browser binary:", BROWSER_PATH);
  const browser = await puppeteer.launch({
    executablePath: BROWSER_PATH,
    headless: true,
    defaultViewport: { width: 1536, height: 960 },
    args: ['--no-sandbox', '--disable-setuid-sandbox', '--window-size=1536,960']
  });

  const page = await browser.newPage();

  // Set default storage state on first visit
  await page.goto(`${BASE_URL}/login`, { waitUntil: 'domcontentloaded', timeout: 15000 });
  await page.evaluate((session, apiBase) => {
    localStorage.setItem("pb_attendance_auth_session_v1", JSON.stringify(session));
    localStorage.setItem("pb_attendance_preferred_api_base", apiBase);
    localStorage.setItem("pb_attendance_api_base", apiBase);
    localStorage.setItem("login_saved_user", "admin");
  }, ADMIN_SESSION, API_BASE);

  for (const r of routes) {
    const targetUrl = `${BASE_URL}${r.path}`;
    const outputPath = path.join(OUTPUT_DIR, `${r.name}.png`);
    console.log(`\nNavigating to ${targetUrl}...`);

    try {
      await page.goto(targetUrl, { waitUntil: 'domcontentloaded', timeout: 15000 });
      await page.evaluate((session, apiBase) => {
        localStorage.setItem("pb_attendance_auth_session_v1", JSON.stringify(session));
        localStorage.setItem("pb_attendance_preferred_api_base", apiBase);
        localStorage.setItem("pb_attendance_api_base", apiBase);
      }, ADMIN_SESSION, API_BASE);
      
      // Short delay for client state hydration
      await new Promise(res => setTimeout(res, 1200));

      await page.screenshot({ path: outputPath, fullPage: false });
      const size = fs.existsSync(outputPath) ? fs.statSync(outputPath).size : 0;
      console.log(`Captured: ${r.name}.png (${size} bytes)`);
    } catch (err) {
      console.error(`Error capturing ${r.name}:`, err.message);
    }
  }

  await browser.close();
  console.log("\nALL SCREENSHOTS CAPTURED SUCCESSFULLY!");
}

run().catch(console.error);
