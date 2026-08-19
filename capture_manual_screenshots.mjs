import fs from 'fs';
import path from 'path';
import { spawn } from 'child_process';

const CHROME_PATH = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
const EDGE_PATH = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const BROWSER_PATH = fs.existsSync(CHROME_PATH) ? CHROME_PATH : EDGE_PATH;
const BASE_URL = "http://127.0.0.1:8073";
const OUTPUT_DIR = path.resolve(process.cwd(), "manual_assets");

if (!fs.existsSync(OUTPUT_DIR)) {
  fs.mkdirSync(OUTPUT_DIR, { recursive: true });
}

const routes = [
  { name: "01_login", url: `${BASE_URL}/login` },
  { name: "02_dashboard", url: `${BASE_URL}/` },
  { name: "03_setup_areas", url: `${BASE_URL}/setup?section=areas` },
  { name: "04_devices", url: `${BASE_URL}/devices` },
  { name: "05_employees", url: `${BASE_URL}/employees` },
  { name: "06_shift_break", url: `${BASE_URL}/shifts?tab=break` },
  { name: "07_shift_timetable", url: `${BASE_URL}/shifts?tab=timetable` },
  { name: "08_shift_setup", url: `${BASE_URL}/shifts?tab=setup` },
  { name: "09_schedule_assignment", url: `${BASE_URL}/assign` },
  { name: "10_schedule_groups", url: `${BASE_URL}/assign?section=groups` },
  { name: "11_schedule_preview", url: `${BASE_URL}/assign?section=preview` },
  { name: "12_access_time_period", url: `${BASE_URL}/access-control?section=time-period` },
  { name: "13_access_groups", url: `${BASE_URL}/access-control?section=groups` },
  { name: "14_access_combination", url: `${BASE_URL}/access-control?section=combination` },
  { name: "15_access_privilege", url: `${BASE_URL}/access-control?section=privilege` },
  { name: "16_transactions", url: `${BASE_URL}/attendance` },
  { name: "17_leaves_holiday", url: `${BASE_URL}/leaves` },
  { name: "18_reports", url: `${BASE_URL}/reports` },
  { name: "19_logs", url: `${BASE_URL}/logs` },
  { name: "20_settings", url: `${BASE_URL}/settings` },
  { name: "21_apply_leave", url: `${BASE_URL}/apply-leave` },
  { name: "22_check_in", url: `${BASE_URL}/check-in` },
];

console.log("Using browser binary:", BROWSER_PATH);

async function captureScreenshots() {
  for (const r of routes) {
    const outputPath = path.join(OUTPUT_DIR, `${r.name}.png`);
    console.log(`Capturing ${r.name} from ${r.url}...`);
    
    const args = [
      "--headless=new",
      "--disable-gpu",
      "--no-sandbox",
      "--window-size=1600,1050",
      `--screenshot=${outputPath}`,
      r.url
    ];

    await new Promise((resolve) => {
      const proc = spawn(BROWSER_PATH, args, { stdio: 'ignore' });
      proc.on('close', resolve);
    });

    if (fs.existsSync(outputPath)) {
      console.log(`Saved screenshot: ${r.name}.png (${fs.statSync(outputPath).size} bytes)`);
    } else {
      console.error(`Failed to capture: ${r.name}`);
    }
  }
  console.log("Screenshot capture complete!");
}

captureScreenshots().catch(console.error);
