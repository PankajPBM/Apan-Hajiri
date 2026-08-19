import fs from 'fs';
import path from 'path';
import puppeteer from 'puppeteer-core';

const CHROME_PATH = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
const EDGE_PATH = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const BROWSER_PATH = fs.existsSync(CHROME_PATH) ? CHROME_PATH : EDGE_PATH;
const ROOT_DIR = path.resolve(process.cwd(), "..");
const ASSETS_DIR = path.join(ROOT_DIR, "manual_assets");
const OUTPUT_HTML = path.join(ROOT_DIR, "E-Attendance_User_Manual.html");
const OUTPUT_PDF = path.join(ROOT_DIR, "E-Attendance_User_Manual.pdf");

function getBase64Img(filename) {
  const filePath = path.join(ASSETS_DIR, filename);
  if (!fs.existsSync(filePath)) {
    console.warn(`Warning: Image ${filename} not found at ${filePath}`);
    return '';
  }
  const fileData = fs.readFileSync(filePath);
  return `data:image/png;base64,${fileData.toString('base64')}`;
}

const images = {
  login: getBase64Img("01_login.png"),
  dashboard: getBase64Img("02_dashboard.png"),
  setup_areas: getBase64Img("03_setup_areas.png"),
  devices: getBase64Img("04_devices.png"),
  employees: getBase64Img("05_employees.png"),
  shift_break: getBase64Img("06_shift_break.png"),
  shift_timetable: getBase64Img("07_shift_timetable.png"),
  shift_setup: getBase64Img("08_shift_setup.png"),
  schedule_assignment: getBase64Img("09_schedule_assignment.png"),
  schedule_groups: getBase64Img("10_schedule_groups.png"),
  schedule_preview: getBase64Img("11_schedule_preview.png"),
  access_time_period: getBase64Img("12_access_time_period.png"),
  access_groups: getBase64Img("13_access_groups.png"),
  access_combination: getBase64Img("14_access_combination.png"),
  access_privilege: getBase64Img("15_access_privilege.png"),
  transactions: getBase64Img("16_transactions.png"),
  leaves_holiday: getBase64Img("17_leaves_holiday.png"),
  reports: getBase64Img("18_reports.png"),
  logs: getBase64Img("19_logs.png"),
  settings: getBase64Img("20_settings.png"),
  apply_leave: getBase64Img("21_apply_leave.png"),
  check_in: getBase64Img("22_check_in.png"),
};

const htmlContent = `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <title>E-Attendance System - Complete User Manual</title>
  <style>
    @import url('https://fonts.googleapis.com/css2?family=Inter:wght@300;400;500;600;700;800&display=swap');

    @page {
      size: A4 portrait;
      margin: 18mm 15mm 20mm 15mm;
    }

    * {
      box-sizing: border-box;
      -webkit-print-color-adjust: exact;
    }

    body {
      font-family: 'Inter', -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
      color: #1e293b;
      background: #ffffff;
      margin: 0;
      padding: 0;
      font-size: 11pt;
      line-height: 1.6;
    }

    /* Cover Page */
    .cover-page {
      page-break-after: always;
      height: 92vh;
      display: flex;
      flex-direction: column;
      justify-content: space-between;
      padding: 50px 30px;
      background: linear-gradient(135deg, #0f172a 0%, #1e3a8a 50%, #0284c7 100%);
      color: #ffffff;
      border-radius: 16px;
    }

    .cover-header {
      margin-top: 30px;
    }

    .cover-badge {
      display: inline-block;
      padding: 6px 16px;
      background: rgba(255, 255, 255, 0.15);
      border: 1px solid rgba(255, 255, 255, 0.3);
      border-radius: 20px;
      font-size: 10pt;
      font-weight: 600;
      letter-spacing: 1px;
      text-transform: uppercase;
      margin-bottom: 25px;
    }

    .cover-title {
      font-size: 34pt;
      font-weight: 800;
      line-height: 1.25;
      margin: 0 0 18px 0;
      color: #ffffff;
    }

    .cover-subtitle {
      font-size: 16pt;
      font-weight: 400;
      color: #93c5fd;
      margin: 0;
    }

    .cover-footer {
      border-top: 1px solid rgba(255, 255, 255, 0.2);
      padding-top: 20px;
      display: flex;
      justify-content: space-between;
      font-size: 10.5pt;
      color: #cbd5e1;
    }

    /* Page Breaks & Layout */
    .page-break {
      page-break-after: always;
    }

    .section-block {
      margin-bottom: 35px;
      page-break-inside: avoid;
    }

    h1.chapter-title {
      font-size: 20pt;
      font-weight: 700;
      color: #0f172a;
      border-bottom: 3px solid #0284c7;
      padding-bottom: 8px;
      margin-top: 20px;
      margin-bottom: 15px;
    }

    h2.section-title {
      font-size: 13.5pt;
      font-weight: 600;
      color: #1e3a8a;
      margin-top: 18px;
      margin-bottom: 10px;
      display: flex;
      align-items: center;
      gap: 8px;
    }

    p {
      margin-top: 0;
      margin-bottom: 12px;
      color: #334155;
      text-align: justify;
    }

    /* Option Badge */
    .option-tag {
      background: #e0f2fe;
      color: #0369a1;
      font-size: 9pt;
      font-weight: 700;
      padding: 3px 10px;
      border-radius: 6px;
      border: 1px solid #bae6fd;
    }

    /* Image Wrapper */
    .screenshot-container {
      margin: 14px 0 20px 0;
      border: 1px solid #cbd5e1;
      border-radius: 8px;
      overflow: hidden;
      box-shadow: 0 4px 12px rgba(0, 0, 0, 0.08);
      background: #f8fafc;
    }

    .screenshot-container img {
      width: 100%;
      height: auto;
      display: block;
    }

    .screenshot-caption {
      background: #f1f5f9;
      padding: 8px 14px;
      font-size: 9pt;
      color: #475569;
      font-weight: 600;
      border-top: 1px solid #e2e8f0;
      text-align: center;
    }

    /* Feature Lists */
    ul.feature-list {
      margin: 8px 0 15px 20px;
      padding: 0;
    }

    ul.feature-list li {
      margin-bottom: 6px;
      color: #334155;
    }

    ul.feature-list strong {
      color: #0f172a;
    }

    /* Callout Box */
    .callout {
      background: #f0f9ff;
      border-left: 4px solid #0284c7;
      padding: 12px 16px;
      border-radius: 0 8px 8px 0;
      margin: 15px 0;
      font-size: 10pt;
    }

    .callout-title {
      font-weight: 700;
      color: #0369a1;
      margin-bottom: 4px;
    }

    /* Table of Contents */
    .toc-table {
      width: 100%;
      border-collapse: collapse;
      margin: 20px 0;
    }

    .toc-table td {
      padding: 10px 12px;
      border-bottom: 1px dashed #cbd5e1;
      font-size: 10.5pt;
    }

    .toc-table tr:hover {
      background: #f8fafc;
    }

    .toc-num {
      font-weight: 700;
      color: #0284c7;
      width: 40px;
    }

    .toc-name {
      font-weight: 600;
      color: #1e293b;
    }

    .toc-desc {
      color: #64748b;
      font-size: 9.5pt;
    }
  </style>
</head>
<body>

  <!-- Cover Page -->
  <div class="cover-page">
    <div class="cover-header">
      <div class="cover-badge">Official Software Manual</div>
      <h1 class="cover-title">E-Attendance System</h1>
      <div class="cover-subtitle">Complete Operational Handbook & Serial Feature Guide</div>
    </div>
    
    <div>
      <p style="color: #cbd5e1; font-size: 11pt; line-height: 1.8;">
        This manual provides a detailed step-by-step walkthrough of all options and features in the <strong>E-Attendance Management Platform</strong>. 
        It covers Biometric Device Management, Push SDK Sync, Shift Scheduling, Access Control, Attendance Processing, Entitlement Leaves, Executive Reports, and System Administration.
      </p>
    </div>

    <div class="cover-footer">
      <div><strong>Developer:</strong> PB Tech Zone</div>
      <div><strong>Version:</strong> v1.0.0 (Release)</div>
      <div><strong>Date:</strong> July 2026</div>
    </div>
  </div>

  <!-- Table of Contents -->
  <div class="page-break">
    <h1 class="chapter-title">Table of Contents & Serial Module Index</h1>
    <p>The following index lists all options serially as available in the E-Attendance sidebar and navigation menu:</p>

    <table class="toc-table">
      <tr><td class="toc-num">01</td><td class="toc-name">Login & Authentication</td><td class="toc-desc">User authentication, license verification, developer contacts</td></tr>
      <tr><td class="toc-num">02</td><td class="toc-name">System Dashboard</td><td class="toc-desc">Real-time attendance counters, device ping status, quick stats</td></tr>
      <tr><td class="toc-num">03</td><td class="toc-name">Organization Setup</td><td class="toc-desc">Areas, Departments, Designations & Employee Categories</td></tr>
      <tr><td class="toc-num">04</td><td class="toc-name">Device Management</td><td class="toc-desc">Biometric hardware integration, Push SDK, PING diagnostic</td></tr>
      <tr><td class="toc-num">05</td><td class="toc-name">Employee Management</td><td class="toc-desc">Master employee profiles, biometric enrollment, department mapping</td></tr>
      <tr><td class="toc-num">06</td><td class="toc-name">Shift Setup - Break Time</td><td class="toc-desc">Meal and refreshment break rules configuration</td></tr>
      <tr><td class="toc-num">07</td><td class="toc-name">Shift Setup - Time Table</td><td class="toc-desc">Working hours, check-in/out windows, late grace period</td></tr>
      <tr><td class="toc-num">08</td><td class="toc-name">Shift Setup - Shifts</td><td class="toc-desc">Regular, flexible, night, and rotating shift master rules</td></tr>
      <tr><td class="toc-num">09</td><td class="toc-name">Schedule Shift - Assignment</td><td class="toc-desc">Individual and departmental shift schedule assignment</td></tr>
      <tr><td class="toc-num">10</td><td class="toc-name">Schedule Shift - Group Management</td><td class="toc-desc">Roster group creation and team scheduling</td></tr>
      <tr><td class="toc-num">11</td><td class="toc-name">Schedule Shift - Preview</td><td class="toc-desc">Monthly and weekly shift calendar matrix preview</td></tr>
      <tr><td class="toc-num">12</td><td class="toc-name">Access Control - Time Period</td><td class="toc-desc">Door access time slots and timezone definitions</td></tr>
      <tr><td class="toc-num">13</td><td class="toc-name">Access Control - Access Group</td><td class="toc-desc">Security access group grouping and door rules</td></tr>
      <tr><td class="toc-num">14</td><td class="toc-name">Access Control - Combination</td><td class="toc-desc">Multi-person unlock verification combinations</td></tr>
      <tr><td class="toc-num">15</td><td class="toc-name">Access Control - Privilege</td><td class="toc-desc">Door access rights assignment per employee</td></tr>
      <tr><td class="toc-num">16</td><td class="toc-name">Transactions</td><td class="toc-desc">Raw biometric logs, manual punch regularization & approvals</td></tr>
      <tr><td class="toc-num">17</td><td class="toc-name">Leaves & Holidays</td><td class="toc-desc">Leave applications, entitlements, holiday master calendar</td></tr>
      <tr><td class="toc-num">18</td><td class="toc-name">Reports & Analytics</td><td class="toc-desc">Daily, Monthly summary, punch details, PDF & Excel export</td></tr>
      <tr><td class="toc-num">19</td><td class="toc-name">Logs</td><td class="toc-desc">System audit trails, user actions, device Push SDK command logs</td></tr>
      <tr><td class="toc-num">20</td><td class="toc-name">Settings</td><td class="toc-desc">Branding (Logo, Color), Database Backup, User Roles (RBAC)</td></tr>
      <tr><td class="toc-num">21</td><td class="toc-name">Self-Service - Apply Leave</td><td class="toc-desc">Employee self-service leave application form</td></tr>
      <tr><td class="toc-num">22</td><td class="toc-name">Self-Service - Web Check-In</td><td class="toc-desc">Browser-based remote check-in / check-out portal</td></tr>
    </table>
  </div>

  <!-- Section 1: Authentication -->
  <div class="section-block">
    <h1 class="chapter-title">1. Authentication & System Access</h1>
    <h2 class="section-title"><span class="option-tag">Option 01</span> System Login Interface</h2>
    <p>
      The E-Attendance login interface secures administrative access. Users authenticate with registered credentials. The login window performs live license verification and displays direct technical support channels.
    </p>
    
    <ul class="feature-list">
      <li><strong>Secure Authentication:</strong> Validates user credentials against encrypted access records.</li>
      <li><strong>License Status Indicator:</strong> Real-time display of system license validity and expiration status.</li>
      <li><strong>Developer Contacts:</strong> Quick access to developer support (PB Tech Zone) via email and WhatsApp.</li>
    </ul>

    <div class="screenshot-container">
      <img src="${images.login}" alt="Login Screen">
      <div class="screenshot-caption">Figure 1.1: E-Attendance System Login Interface</div>
    </div>
  </div>

  <div class="page-break"></div>

  <!-- Section 2: Dashboard -->
  <div class="section-block">
    <h1 class="chapter-title">2. Executive Dashboard</h1>
    <h2 class="section-title"><span class="option-tag">Option 02</span> Real-Time Attendance Console</h2>
    <p>
      The main dashboard displays an executive summary of today's attendance metrics across all departments, real-time biometric punch feeds, connected device health, and action shortcuts.
    </p>

    <ul class="feature-list">
      <li><strong>Today's Status Widgets:</strong> Live counters for Present, Absent, Late In, Early Out, and Leave.</li>
      <li><strong>Device PING Cards:</strong> Shows active biometric hardware terminals and network connectivity.</li>
      <li><strong>Recent Punch Stream:</strong> Live feed of incoming biometric logs with timestamps and employee details.</li>
    </ul>

    <div class="screenshot-container">
      <img src="${images.dashboard}" alt="Dashboard Screen">
      <div class="screenshot-caption">Figure 2.1: Executive System Dashboard</div>
    </div>
  </div>

  <div class="page-break"></div>

  <!-- Section 3: Setup -->
  <div class="section-block">
    <h1 class="chapter-title">3. Organization Master Setup</h1>
    <h2 class="section-title"><span class="option-tag">Option 03</span> Areas, Departments & Categories</h2>
    <p>
      Structure your company hierarchy by defining geographical Areas, functional Departments, Designations, and Employee Categories for precise attendance and reporting rules.
    </p>

    <ul class="feature-list">
      <li><strong>Multi-Area Setup:</strong> Group branch offices or project locations.</li>
      <li><strong>Department Structure:</strong> Link sub-departments under main corporate divisions.</li>
      <li><strong>Category Classifications:</strong> Define rules for permanent, contract, or shift personnel.</li>
    </ul>

    <div class="screenshot-container">
      <img src="${images.setup_areas}" alt="Setup Screen">
      <div class="screenshot-caption">Figure 3.1: Organizational Setup (Areas & Departments)</div>
    </div>
  </div>

  <div class="page-break"></div>

  <!-- Section 4: Devices -->
  <div class="section-block">
    <h1 class="chapter-title">4. Device Management</h1>
    <h2 class="section-title"><span class="option-tag">Option 04</span> Biometric Hardware & Push SDK</h2>
    <p>
      Manage connected fingerprint, face recognition, and RFID card terminals. Supports real-time Push SDK communication (ADMS/ICLK), instant command queuing, and diagnostic PING checks.
    </p>

    <div class="callout">
      <div class="callout-title">Push SDK Command Spec</div>
      Hardware command IDs must use short 13-character alias strings to conform to ZKTeco PUSH specifications and prevent device memory buffer overruns.
    </div>

    <ul class="feature-list">
      <li><strong>Terminal Registration:</strong> Add hardware devices by Serial Number, IP, and assigned Area.</li>
      <li><strong>Live PING Test:</strong> Verify network connectivity directly from the admin panel.</li>
      <li><strong>Remote Command Queue:</strong> Sync user profiles, clear logs, and push firmware commands.</li>
    </ul>

    <div class="screenshot-container">
      <img src="${images.devices}" alt="Devices Screen">
      <div class="screenshot-caption">Figure 4.1: Biometric Device Management Panel</div>
    </div>
  </div>

  <div class="page-break"></div>

  <!-- Section 5: Employees -->
  <div class="section-block">
    <h1 class="chapter-title">5. Employee Directory</h1>
    <h2 class="section-title"><span class="option-tag">Option 05</span> Master Employee Profiles</h2>
    <p>
      Maintain central employee master records, biometric card numbers, face IDs, mobile contacts, and department assignments.
    </p>

    <ul class="feature-list">
      <li><strong>Profile Management:</strong> Record employee code, full name, joining date, and contact details.</li>
      <li><strong>Biometric Enrollment:</strong> Associate RFID card numbers and fingerprint IDs to hardware devices.</li>
      <li><strong>Spreadsheet Import/Export:</strong> Batch import employee lists directly from Excel spreadsheets.</li>
    </ul>

    <div class="screenshot-container">
      <img src="${images.employees}" alt="Employees Screen">
      <div class="screenshot-caption">Figure 5.1: Master Employee Directory</div>
    </div>
  </div>

  <div class="page-break"></div>

  <!-- Section 6: Shift Setup -->
  <div class="section-block">
    <h1 class="chapter-title">6. Shift & Time Table Setup</h1>

    <h2 class="section-title"><span class="option-tag">Option 06</span> Break Time Setup</h2>
    <p>Configure paid or unpaid lunch, tea, and operational breaks linked to specific working shifts.</p>
    <div class="screenshot-container">
      <img src="${images.shift_break}" alt="Break Time Screen">
      <div class="screenshot-caption">Figure 6.1: Break Time Configuration Window</div>
    </div>

    <h2 class="section-title"><span class="option-tag">Option 07</span> Time Table Master</h2>
    <p>Define standard check-in/check-out timing windows, late grace periods, early leave thresholds, and overtime start rules.</p>
    <div class="screenshot-container">
      <img src="${images.shift_timetable}" alt="Time Table Screen">
      <div class="screenshot-caption">Figure 6.2: Time Table Parameters</div>
    </div>

    <div class="page-break"></div>

    <h2 class="section-title"><span class="option-tag">Option 08</span> Shift Rules Setup</h2>
    <p>Combine time tables into complete shift routines (e.g., General Morning Shift, Evening Shift, Night Shift, Flexible Shift) with weekend off rules.</p>
    <div class="screenshot-container">
      <img src="${images.shift_setup}" alt="Shift Setup Screen">
      <div class="screenshot-caption">Figure 6.3: Shift Rules Setup Panel</div>
    </div>
  </div>

  <div class="page-break"></div>

  <!-- Section 7: Schedule Shift -->
  <div class="section-block">
    <h1 class="chapter-title">7. Shift Scheduling & Rostering</h1>

    <h2 class="section-title"><span class="option-tag">Option 09</span> Schedule Assignment</h2>
    <p>Assign configured shifts to individual staff members or entire departmental units for specified date ranges.</p>
    <div class="screenshot-container">
      <img src="${images.schedule_assignment}" alt="Schedule Assignment Screen">
      <div class="screenshot-caption">Figure 7.1: Schedule Assignment Console</div>
    </div>

    <h2 class="section-title"><span class="option-tag">Option 10</span> Roster Group Management</h2>
    <p>Organize employees into operational roster groups for easy rotational shift scheduling.</p>
    <div class="screenshot-container">
      <img src="${images.schedule_groups}" alt="Group Management Screen">
      <div class="screenshot-caption">Figure 7.2: Roster Group Management</div>
    </div>

    <div class="page-break"></div>

    <h2 class="section-title"><span class="option-tag">Option 11</span> Monthly Schedule Preview</h2>
    <p>Visual monthly matrix displaying assigned shifts per employee per day for roster validation before execution.</p>
    <div class="screenshot-container">
      <img src="${images.schedule_preview}" alt="Schedule Preview Screen">
      <div class="screenshot-caption">Figure 7.3: Monthly Shift Schedule Matrix Preview</div>
    </div>
  </div>

  <div class="page-break"></div>

  <!-- Section 8: Access Control -->
  <div class="section-block">
    <h1 class="chapter-title">8. Hardware Access Control</h1>

    <h2 class="section-title"><span class="option-tag">Option 12</span> Access Time Period</h2>
    <p>Define time slots during which door unlocking is permitted on biometric physical access controllers.</p>
    <div class="screenshot-container">
      <img src="${images.access_time_period}" alt="Time Period Screen">
      <div class="screenshot-caption">Figure 8.1: Access Control Time Period Setup</div>
    </div>

    <h2 class="section-title"><span class="option-tag">Option 13</span> Access Group Rules</h2>
    <p>Group time slots and door rights into designated access groups for streamlined security management.</p>
    <div class="screenshot-container">
      <img src="${images.access_groups}" alt="Access Group Screen">
      <div class="screenshot-caption">Figure 8.2: Access Group Configuration</div>
    </div>

    <div class="page-break"></div>

    <h2 class="section-title"><span class="option-tag">Option 14</span> Multi-Verification Combination</h2>
    <p>Configure multi-person unlock combinations requiring 2 or more authorized users to unlock high-security doors.</p>
    <div class="screenshot-container">
      <img src="${images.access_combination}" alt="Combination Screen">
      <div class="screenshot-caption">Figure 8.3: Door Unlock Combination Setup</div>
    </div>

    <h2 class="section-title"><span class="option-tag">Option 15</span> Door Access Privilege</h2>
    <p>Grant or revoke specific door privileges per employee across network devices.</p>
    <div class="screenshot-container">
      <img src="${images.access_privilege}" alt="Privilege Screen">
      <div class="screenshot-caption">Figure 8.4: User Door Access Privilege Controller</div>
    </div>
  </div>

  <div class="page-break"></div>

  <!-- Section 9: Attendance Transactions -->
  <div class="section-block">
    <h1 class="chapter-title">9. Attendance Transactions</h1>
    <h2 class="section-title"><span class="option-tag">Option 16</span> Transaction Logs & Regularization</h2>
    <p>
      View incoming raw biometric transaction logs, perform manual punch adjustments (for missed punches), and process manager approvals for attendance regularization.
    </p>

    <ul class="feature-list">
      <li><strong>Raw Log Search:</strong> Filter punches by date range, department, employee code, or punch mode.</li>
      <li><strong>Manual Punch Entry:</strong> Add authorized manual in/out entries with reason audit tags.</li>
      <li><strong>Attendance Reprocessing:</strong> Recalculate daily attendance status after shift or punch modifications.</li>
    </ul>

    <div class="screenshot-container">
      <img src="${images.transactions}" alt="Transactions Screen">
      <div class="screenshot-caption">Figure 9.1: Attendance Transactions & Punch Logs</div>
    </div>
  </div>

  <div class="page-break"></div>

  <!-- Section 10: Leaves & Holidays -->
  <div class="section-block">
    <h1 class="chapter-title">10. Leaves & Holiday Management</h1>
    <h2 class="section-title"><span class="option-tag">Option 17</span> Leave Records & Public Holidays</h2>
    <p>
      Manage employee leave entitlements (Casual Leave, Sick Leave, Earned Leave), submit leave applications, and publish the annual organization holiday calendar.
    </p>

    <ul class="feature-list">
      <li><strong>Leave Processing:</strong> Approve or reject employee leave requests with supervisor notes.</li>
      <li><strong>Holiday Master:</strong> Set recurring public holidays and weekend off schedules.</li>
      <li><strong>Entitlement Quotas:</strong> Automatic deduction of approved leaves from annual entitlement quotas.</li>
    </ul>

    <div class="screenshot-container">
      <img src="${images.leaves_holiday}" alt="Leaves & Holidays Screen">
      <div class="screenshot-caption">Figure 10.1: Leaves & Public Holiday Master</div>
    </div>
  </div>

  <div class="page-break"></div>

  <!-- Section 11: Reports -->
  <div class="section-block">
    <h1 class="chapter-title">11. Executive Reports & Analytics</h1>
    <h2 class="section-title"><span class="option-tag">Option 18</span> Attendance Reporting Center</h2>
    <p>
      Generate comprehensive daily attendance sheets, monthly summary matrices, late/early exception lists, and overtime calculations. Supports one-click export to PDF and Excel formats.
    </p>

    <ul class="feature-list">
      <li><strong>Daily Attendance Report:</strong> Detailed list of punch times, work hours, and status per staff member.</li>
      <li><strong>Monthly Attendance Sheet:</strong> Comprehensive grid view showing daily status codes (P, A, L, H, W).</li>
      <li><strong>Export Capabilities:</strong> Export clean formatted reports directly for payroll integration.</li>
    </ul>

    <div class="screenshot-container">
      <img src="${images.reports}" alt="Reports Screen">
      <div class="screenshot-caption">Figure 11.1: Executive Attendance Reports Generator</div>
    </div>
  </div>

  <div class="page-break"></div>

  <!-- Section 12: Logs -->
  <div class="section-block">
    <h1 class="chapter-title">12. System Audit & Communication Logs</h1>
    <h2 class="section-title"><span class="option-tag">Option 19</span> Audit Trail & Device Logs</h2>
    <p>
      Monitor system activity logs, user logins, data modifications, and Push SDK hardware communication logs to ensure accountability and system integrity.
    </p>

    <div class="screenshot-container">
      <img src="${images.logs}" alt="System Logs Screen">
      <div class="screenshot-caption">Figure 12.1: System Audit & Device Communication Logs</div>
    </div>
  </div>

  <div class="page-break"></div>

  <!-- Section 13: Settings -->
  <div class="section-block">
    <h1 class="chapter-title">13. System Settings & Customization</h1>
    <h2 class="section-title"><span class="option-tag">Option 20</span> Branding, Backup & User Roles</h2>
    <p>
      Configure system branding (Organization Name, Custom Logo, Primary Color Palette), manage user accounts with Role-Based Access Control (RBAC), and perform database backups/restores.
    </p>

    <ul class="feature-list">
      <li><strong>White-Label Customization:</strong> Upload company logo and select custom sidebar themes.</li>
      <li><strong>Role-Based Security:</strong> Manage Admin, Manager, and Operator user privileges per module.</li>
      <li><strong>Database Backup & Restore:</strong> Create point-in-time system data backups.</li>
    </ul>

    <div class="screenshot-container">
      <img src="${images.settings}" alt="Settings Screen">
      <div class="screenshot-caption">Figure 13.1: System Settings & White-Label Customization</div>
    </div>
  </div>

  <div class="page-break"></div>

  <!-- Section 14: ESS Portal -->
  <div class="section-block">
    <h1 class="chapter-title">14. Employee Self-Service (ESS)</h1>

    <h2 class="section-title"><span class="option-tag">Option 21</span> Self-Service Leave Portal</h2>
    <p>Allows employees to submit leave requests online with dates, leave type selection, and justification notes.</p>
    <div class="screenshot-container">
      <img src="${images.apply_leave}" alt="Apply Leave Screen">
      <div class="screenshot-caption">Figure 14.1: Self-Service Leave Application Portal</div>
    </div>

    <h2 class="section-title"><span class="option-tag">Option 22</span> Web Check-In Portal</h2>
    <p>Enables remote or field staff to log punch-in and punch-out events directly from web browser devices.</p>
    <div class="screenshot-container">
      <img src="${images.check_in}" alt="Web Check-in Screen">
      <div class="screenshot-caption">Figure 14.2: Web Check-In / Check-Out Interface</div>
    </div>
  </div>

</body>
</html>`;

fs.writeFileSync(OUTPUT_HTML, htmlContent, 'utf-8');
console.log(`Generated HTML manual at: ${OUTPUT_HTML} (${fs.statSync(OUTPUT_HTML).size} bytes)`);

async function generatePDF() {
  console.log("Rendering PDF manual using Puppeteer...");
  const browser = await puppeteer.launch({
    executablePath: BROWSER_PATH,
    headless: true,
    args: ['--no-sandbox', '--disable-setuid-sandbox']
  });

  const page = await browser.newPage();
  await page.goto(`file:///${OUTPUT_HTML.replace(/\\/g, '/')}`, { waitUntil: 'networkidle0', timeout: 60000 });

  await page.pdf({
    path: OUTPUT_PDF,
    format: 'A4',
    printBackground: true,
    margin: {
      top: '18mm',
      bottom: '20mm',
      left: '15mm',
      right: '15mm'
    },
    displayHeaderFooter: true,
    headerTemplate: `<div style="font-family: Arial; font-size: 8pt; color: #94a3b8; width: 100%; text-align: right; padding-right: 15mm;">E-Attendance User Manual</div>`,
    footerTemplate: `<div style="font-family: Arial; font-size: 8pt; color: #94a3b8; width: 100%; display: flex; justify-content: space-between; padding: 0 15mm;"><span>PB Tech Zone</span><span>Page <span class="pageNumber"></span> of <span class="totalPages"></span></span></div>`
  });

  await browser.close();
  console.log(`\nPDF USER MANUAL GENERATED SUCCESSFULLY: ${OUTPUT_PDF} (${fs.statSync(OUTPUT_PDF).size} bytes)`);
}

generatePDF().catch(console.error);
