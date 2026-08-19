import fs from 'fs';
import path from 'path';
import puppeteer from 'puppeteer-core';

const CHROME_PATH = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
const EDGE_PATH = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const BROWSER_PATH = fs.existsSync(CHROME_PATH) ? CHROME_PATH : EDGE_PATH;
const BASE_URL = "http://127.0.0.1:8073";
const OUTPUT_DIR = path.resolve(process.cwd(), "manual_assets");

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
  { name: "01_login", path: "/login" },
  { name: "02_dashboard", path: "/" },
  { name: "03_setup_areas", path: "/setup?section=areas" },
  { name: "04_devices", path: "/devices" },
  { name: "05_employees", path: "/employees" },
  { name: "06_shift_break", path: "/shifts?tab=break" },
  { name: "07_shift_timetable", path: "/shifts?tab=timetable" },
  { name: "08_shift_setup", path: "/shifts?tab=setup" },
  { name: "09_schedule_assignment", path: "/assign" },
  { name: "10_schedule_groups", path: "/assign?section=groups" },
  { name: "11_schedule_preview", path: "/assign?section=preview" },
  { name: "12_access_time_period", path: "/access-control?section=time-period" },
  { name: "13_access_groups", path: "/access-control?section=groups" },
  { name: "14_access_combination", path: "/access-control?section=combination" },
  { name: "15_access_privilege", path: "/access-control?section=privilege" },
  { name: "16_transactions", path: "/attendance" },
  { name: "17_leaves_holiday", path: "/leaves" },
  { name: "18_reports", path: "/reports" },
  { name: "19_logs", path: "/logs" },
  { name: "20_settings", path: "/settings" },
  { name: "21_apply_leave", path: "/apply-leave" },
  { name: "22_check_in", path: "/check-in" },
];

async function run() {
  console.log("Launching browser:", BROWSER_PATH);
  const browser = await puppeteer.launch({
    executablePath: BROWSER_PATH,
    headless: true,
    defaultViewport: { width: 1600, height: 1000 },
    args: ['--no-sandbox', '--disable-setuid-sandbox']
  });

  const page = await browser.newPage();

  // Set LocalStorage for Auth Session
  await page.goto(`${BASE_URL}/login`, { waitUntil: 'networkidle2' });
  await page.evaluate((session) => {
    localStorage.setItem("pb_attendance_auth_session_v1", JSON.stringify(session));
  }, ADMIN_SESSION);

  for (const r of routes) {
    const targetUrl = `${BASE_URL}${r.path}`;
    const outputPath = path.join(OUTPUT_DIR, `${r.name}.png`);
    console.log(`Navigating to ${targetUrl}...`);
    
    try {
      await page.goto(targetUrl, { waitUntil: 'networkidle2', timeout: 15000 });
      await new Promise(res => setTimeout(res, 1200)); // allow animations/charts to settle
      await page.screenshot({ path: outputPath, fullPage: false });
      console.log(`Captured: ${r.name}.png`);
    } catch (err) {
      console.error(`Error capturing ${r.name}:`, err.message);
    }
  }

  await browser.close();
  console.log("Screenshot capture finished!");
}

run().catch(console.error);
