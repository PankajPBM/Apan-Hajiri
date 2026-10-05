import fs from 'fs';
import path from 'path';
import puppeteer from './e-attendance-next/node_modules/puppeteer-core/lib/puppeteer/puppeteer-core.js';

const CHROME_PATH = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
const EDGE_PATH = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const BROWSER_PATH = fs.existsSync(CHROME_PATH) ? CHROME_PATH : EDGE_PATH;
const BASE_URL = "http://127.0.0.1:8073";
const STAGING_DIR = path.resolve(process.cwd(), "installer", "staging_png");

if (!fs.existsSync(STAGING_DIR)) {
  fs.mkdirSync(STAGING_DIR, { recursive: true });
}

const ADMIN_SESSION = {
  userId: "default-admin",
  username: "admin",
  fullName: "Admin User",
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

const BRANDING = {
  organizationName: "अपन हाजिरी",
  systemTitle: "Apan Hajiri",
  logoUrl: "/apan_logo.png"
};

const slides = [
  { name: "slide_dashboard", path: "/" },
  { name: "slide_login", path: "/login", isLogin: true },
  { name: "slide_employees", path: "/employees" },
  { name: "slide_devices", path: "/devices" },
  { name: "slide_shifts", path: "/shifts" },
  { name: "slide_attendance", path: "/attendance" },
  { name: "slide_leaves", path: "/leaves" },
  { name: "slide_reports", path: "/reports" },
  { name: "slide_settings", path: "/settings" },
  { name: "slide_setup", path: "/setup" },
  { name: "slide_access", path: "/access-control" },
];

async function captureAll() {
  console.log("Launching headless browser from:", BROWSER_PATH);
  const browser = await puppeteer.launch({
    executablePath: BROWSER_PATH,
    headless: true,
    defaultViewport: {
      width: 1240,
      height: 590,
      deviceScaleFactor: 2
    },
    args: [
      '--no-sandbox',
      '--disable-setuid-sandbox',
      '--hide-scrollbars',
      '--disable-notifications'
    ]
  });

  const page = await browser.newPage();
  await page.setRequestInterception(true);

  page.on('request', (req) => {
    const url = req.url();

    // Mock API requests so every screen displays clean, realistic preview data with zero errors
    if (url.includes('/api/devices')) {
      if (url.includes('/commands')) {
        return req.respond({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ totalCount: 0, items: [] })
        });
      }
      return req.respond({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          totalCount: 2,
          page: 1,
          pageSize: 200,
          items: [
            {
              id: "dev-1",
              serialNumber: "ZK-BIO-9021",
              name: "Main Entrance Biometric",
              model: "ZKTeco MB20-VL",
              ipAddress: "192.168.1.201",
              area: "Headquarters",
              location: "Front Reception",
              department: "Administration",
              state: "Online",
              isOnline: true,
              userQty: 48,
              fpQty: 96,
              faceQty: 48,
              palmQty: 0,
              transactionQty: 2450,
              cmdQty: 0,
              lastSeenUtc: new Date().toISOString()
            },
            {
              id: "dev-2",
              serialNumber: "ZK-BIO-9022",
              name: "Warehouse Door Terminal",
              model: "ZKTeco K40",
              ipAddress: "192.168.1.202",
              area: "Branch 1",
              location: "Warehouse Entry",
              department: "Logistics",
              state: "Online",
              isOnline: true,
              userQty: 24,
              fpQty: 48,
              faceQty: 0,
              palmQty: 0,
              transactionQty: 980,
              cmdQty: 0,
              lastSeenUtc: new Date().toISOString()
            }
          ]
        })
      });
    }

    if (url.includes('/api/departments')) {
      return req.respond({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          { id: "1", code: "IT", name: "Information Technology" },
          { id: "2", code: "HR", name: "Human Resources" },
          { id: "3", code: "OPS", name: "Operations" },
          { id: "4", code: "MGMT", name: "Management" }
        ])
      });
    }

    if (url.includes('/api/areas')) {
      return req.respond({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          { id: "1", areaCode: "HQ", areaName: "Headquarters" },
          { id: "2", areaCode: "BR1", areaName: "Branch Office" }
        ])
      });
    }

    if (url.includes('/api/employees')) {
      return req.respond({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          totalCount: 3,
          page: 1,
          pageSize: 50,
          items: [
            { id: "1", employeeId: "EMP-001", name: "Pankaj Sharma", department: "Engineering", position: "Lead Architect", cardNo: "10021", isActive: true, isOnline: true },
            { id: "2", employeeId: "EMP-002", name: "Aayush Thapa", department: "Operations", position: "Operations Manager", cardNo: "10022", isActive: true, isOnline: true },
            { id: "3", employeeId: "EMP-003", name: "Sunil Shrestha", department: "Administration", position: "Executive Officer", cardNo: "10023", isActive: true, isOnline: true }
          ]
        })
      });
    }

    // Default: continue standard requests
    req.continue();
  });

  for (const slide of slides) {
    const targetUrl = `${BASE_URL}${slide.path}`;
    const outputPath = path.join(STAGING_DIR, `${slide.name}.png`);
    console.log(`[Capturing] ${slide.name} from ${targetUrl}...`);

    try {
      if (slide.isLogin) {
        // Clear auth session for login screen
        await page.goto(`${BASE_URL}/login`, { waitUntil: 'networkidle2', timeout: 20000 });
        await page.evaluate(() => {
          localStorage.removeItem("pb_attendance_auth_session_v1");
        });
        await page.reload({ waitUntil: 'networkidle2', timeout: 20000 });
      } else {
        // Inject auth session and branding into localStorage
        await page.goto(targetUrl, { waitUntil: 'domcontentloaded', timeout: 20000 });
        await page.evaluate((session, branding) => {
          localStorage.setItem("pb_attendance_auth_session_v1", JSON.stringify(session));
          localStorage.setItem("pb_attendance_branding_v1", JSON.stringify(branding));
        }, ADMIN_SESSION, BRANDING);
        await page.goto(targetUrl, { waitUntil: 'networkidle2', timeout: 20000 });
      }

      // Hide Next.js dev portal overlays and badges for a pristine clean look
      await page.addStyleTag({
        content: `
          nextjs-portal,
          [data-nextjs-toast],
          #__next-build-watcher,
          div[data-nextjs-dialog],
          .next-error-badge {
            display: none !important;
            visibility: hidden !important;
          }
        `
      });

      // Allow charts, animations, and icons to settle
      await new Promise((resolve) => setTimeout(resolve, 1500));

      await page.screenshot({ path: outputPath, fullPage: false });
      console.log(`✓ Saved ${slide.name}.png`);
    } catch (err) {
      console.error(`✗ Error on ${slide.name}:`, err.message);
    }
  }

  await browser.close();
  console.log("All slides captured successfully to:", STAGING_DIR);
}

captureAll().catch((err) => {
  console.error("Capture process failed:", err);
  process.exit(1);
});
