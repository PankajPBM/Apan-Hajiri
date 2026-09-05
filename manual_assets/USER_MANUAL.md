# Apan Hajiri (E-Attendance) — Complete Step-by-Step User Manual

---

## Table of Contents
1. [System Introduction & Architecture](#1-system-introduction--architecture)
2. [Service Controller (Windows Desktop Management & Nuclear Repair)](#2-service-controller-windows-desktop-management)
3. [User Login & Authentication](#3-user-login--authentication)
4. [Dashboard & Live Telemetry](#4-dashboard--live-telemetry)
5. [Organizational Setup (Company, Departments, Designations & Areas)](#5-organizational-setup)
6. [Device Management & Biometric Synchronization](#6-device-management--biometric-synchronization)
7. [Employee Directory & Biometrics Enrollment](#7-employee-directory--biometrics-enrollment)
8. [Shifts, Timetables, & Break Rules](#8-shifts-timetables--break-rules)
9. [Schedule Assignment & Shift Rosters](#9-schedule-assignment--shift-rosters)
10. [Access Control (Time Periods, Groups, & Multi-Group Combinations)](#10-access-control)
11. [Live Transactions & Attendance Logs](#11-live-transactions--attendance-logs)
12. [Leaves & Nepali Bikram Sambat (BS) Calendar Integration](#12-leaves--nepali-bikram-sambat-bs-calendar-integration)
13. [Attendance Reports & Data Export](#13-attendance-reports--data-export)
14. [System Settings, Database Management & Backup/Restore](#14-system-settings-database-management--backuprestore)
15. [Troubleshooting & Administrator FAQ](#15-troubleshooting--administrator-faq)

---

## 1. System Introduction & Architecture

**Apan Hajiri (E-Attendance)** is an enterprise-grade time attendance and physical access control management platform designed for corporate offices, factories, hospitals, universities, and multi-branch enterprises.

### Core Architectural Features:
- **Dual Calendar Engine:** Native support for the **Nepali Bikram Sambat (BS)** calendar (including 2081, 2082, and 2083 BS) alongside the standard Gregorian (AD) calendar.
- **Biometric Terminal Integration:** Native ZKTeco and compatible hardware communication over TCP/IP and Cloud PUSH protocols using safe 13-character command IDs.
- **PostgreSQL Database:** Industrial-strength relational data storage with auto-bootstrapped schemas and scheduled backup automation.
- **Resilient Windows Background Services:** Runs background services (`E-Attendance-API` on port `8081` and `E-Attendance-Web` on port `8001`) with automatic restart policies.

---

## 2. Service Controller (Windows Desktop Management)

The Service Controller is a standalone desktop management console that provides complete control over the system background services, database engine, ports, and automated repairs.

![Service Controller & Settings](Settings.png)

### Operational Controls:
1. **Central Master Power Button:**
   - **Click when Stopped (Red):** Sequentially checks PostgreSQL connectivity, runs schema auto-recovery, and starts both Windows services.
   - **Click when Running (Green):** Performs a clean, graceful shutdown of all background services and active network listeners.
2. **Real-time CPU Graph:** Live processor utilization tracking for host hardware monitoring.
3. **API & Web UI Service Rows:**
   - Displays real-time status (`Running` / `Stopped`).
   - Individual **Start** and **Stop** buttons for surgical component troubleshooting.
4. **⚙ Fix Services Button (Guarded Nuclear Repair):**
   - **Warning Confirmation:** Prompts a prominent warning dialog before proceeding:
     > **⚠️ WARNING: Fix & Reinstall Services**  
     > *This will stop all running services, cleanly reinstall and re-register both Windows services, re-apply firewall security rules, and restart all services fresh. Active device sync and web connections will temporarily pause during restart. Are you sure?*
   - **Action:** Halts orphaned host processes, re-registers both Windows services (`E-Attendance-API` and `E-Attendance-Web`) with automatic restart policies, updates firewall rules, verifies database schema, and starts everything fresh.
5. **Port Customization:** Change the API port (default: `8081`) or Web UI port (default: `8001`) and click **Save Ports**. Firewall exclusions update automatically.
6. **Database Backup & Restore:** Create compressed snapshots or restore existing `.psql` backups with one click.

---

## 3. User Login & Authentication

Access the web portal through your web browser at `http://localhost:8001` (or your configured server IP and port).

![Login Screen](Login%20Dashboard.png)

### Step-by-Step Login:
1. Enter your registered **Username** (Default administrator: `admin`).
2. Enter your secure **Password**.
3. Click **Sign In**.
4. Upon successful authentication, your session token is established and you are redirected to the live Dashboard.

---

## 4. Dashboard & Live Telemetry

The main dashboard is the real-time operational nerve center of Apan Hajiri.

![Main Dashboard](Dashboard.png)

### Key Dashboard Sections:
1. **Live Presence Metric Cards (Top Bar):**
   - **Present Today:** Total unique staff members checked in for the current date.
   - **Late Arrivals:** Personnel who punched after their assigned timetable grace period.
   - **Early Leaves:** Employees who punched out prior to shift completion.
   - **On Leave:** Personnel with approved leave for the day.
   - **Absent:** Employees with no punches recorded during their scheduled timetable.
2. **Biometric Device Connection Grid:**
   - Real-time online/offline connection indicators for every registered hardware terminal.
   - Live IP address, latency, and synchronization health status.
3. **Recent Attendance Punch Stream:**
   - Live streaming list of the most recent employee verifications across all terminals with employee name, department, time, and verification mode.

---

## 5. Organizational Setup

Establish your company hierarchy before enrolling employees and scheduling shifts.

![Setup Areas & Structure](Setup.png)

### Configuration Steps:
1. **Company Profile:** Set company name, legal tax/PAN registration, contact info, and official logo.
2. **Departments:**
   - Click **Add Department**.
   - Enter department title (e.g., *Engineering*, *Human Resources*, *Operations*).
   - Assign parent department if creating multi-tier hierarchies.
3. **Designations / Positions:** Define organizational titles and job roles for automated permission mapping.
4. **Physical Areas / Zones:**
   - Create physical zones (e.g., *Head Office Main Gate*, *Factory Floor*, *Warehouse Turnstile*).
   - Assign biometric devices to specific areas so transactions are cleanly grouped.

---

## 6. Device Management & Biometric Synchronization

Manage all physical biometric terminals, RFID card scanners, and facial recognition devices.

![Device Management](Devices.png)

### Registering a New Terminal:
1. Navigate to **Devices** in the main navigation sidebar.
2. Click **+ Add Device**.
3. Configure terminal connection parameters:
   - **Device Name:** Unique identifier (e.g., *Main Entrance Face Scanner*).
   - **IP Address:** Static local network IP of the hardware terminal (e.g., `192.168.1.201`).
   - **Port:** Default is `4370`.
   - **Communication Protocol:** Select **TCP/IP** (direct polling) or **Cloud PUSH** (device-initiated HTTP push).
   - **Communication Password:** Enter if configured on the device (default: `0` or blank).
4. Click **Test Connection** to verify reachability.
5. Click **Save**.

### Device Actions Menu:
- **Sync Employees:** Pushes newly created employee names, PINs, RFID cards, and biometric templates to the device.
- **Fetch Transactions:** Pulls all uncollected punch records stored in device memory.
- **Synchronize Time:** Calibrates the terminal's internal hardware clock with the server's time.
- **Remote Reboot:** Restarts the device over the network.
- **Clear Device Logs:** Clears internal device storage after confirming all records are safely saved in PostgreSQL.

---

## 7. Employee Directory & Biometrics Enrollment

Manage personnel records, credentials, and biometric template registrations.

![Employee Directory](Employees.png)

### Adding an Employee:
1. Navigate to **Employees** -> Click **+ Add Employee**.
2. **Personal Information:** Enter Full Name, Gender, Date of Birth, Contact Number, and Email.
3. **Employment Details:**
   - **Badge / Employee ID:** Official company serial number.
   - **Device PIN:** Numeric ID entered on biometric terminals (must be unique).
   - **Department:** Select from organizational setup.
   - **Designation:** Select assigned job title.
   - **Joining Date:** Supports both AD and BS date inputs.
4. **Biometric Credentials:**
   - **RFID Card Number:** Input 10-digit card serial number.
   - **Fingerprint & Face Templates:** Can be enrolled directly on any connected terminal and synced to the database via **Sync From Device**.
5. **Bulk Import via Excel:**
   - Click **Import Excel**.
   - Download the pre-formatted template.
   - Fill in your staff roster and upload for instantaneous mass onboarding.

---

## 8. Shifts, Timetables, & Break Rules

Define flexible working hours, overtime thresholds, and automated break deductions.

![Shift and Timetable Setup](Shift%20and%20Timetable.png)

### 1. Creating a Timetable:
- **Timetable Name:** (e.g., *General Day 9 to 5*).
- **Check-In Window:** Earliest valid check-in (e.g., `08:00`) to latest valid check-in (e.g., `10:30`).
- **Standard On-Duty Time:** Expected arrival time (e.g., `09:00`).
- **Late Grace Period:** Allowable late tolerance in minutes (e.g., `15 Minutes`). Punches before `09:15` are marked *On Time*.
- **Standard Off-Duty Time:** Expected departure time (e.g., `17:00`).
- **Early Exit Tolerance:** Minutes allowed to leave before official off-duty without penalty.
- **Minimum Hours for Half Day:** (e.g., `4 Hours`).

### 2. Configuring Break Schedules:
- Set break intervals (e.g., *Lunch: 13:00 - 14:00*).
- Choose **Auto Deduct** (automatically subtracts break duration from total daily hours) or **Punch Required** (validates that employees punched out and back in).

### 3. Assembling Shifts:
- Combine timetables into recurring schedules (Day Shift, Night Shift, Rotational Roster).
- Night shifts crossing midnight are handled automatically by the date-matching algorithm.

---

## 9. Schedule Assignment & Shift Rosters

Assign created shifts to personnel and manage temporary schedule swaps.

![Schedule Assignment](Shift%20and%20Timetable.png)

### Assignment Options:
1. **Department Master Assignment:** Assign a default shift to an entire department. All current and future members inherit this schedule.
2. **Group Assignment:** Create specific duty rosters (e.g., *Security Guard Shift A*, *Weekend Support*).
3. **Individual Schedule Override:** Set a custom timetable for a specific employee over a designated date range.
4. **Roster Calendar Preview:** Inspect color-coded calendar grids to verify employee schedules day by day.

---

## 10. Access Control

Configure high-security physical door access, unlock schedules, and dual-custody combinations.

![Access Control - Time Periods](Acess%20Control%20-%20Time%20Period.png)

### 1. Time Periods (1 to 50):
- Configure up to 50 weekly schedules.
- For each day (Monday through Sunday), define up to 3 active unlock time spans (e.g., `08:00 - 18:00`).
- Outside active time periods, doors remain locked regardless of card scans.

### 2. Door Access Groups:
- Group doors and terminals into security zones.
- Bind specific Time Periods to Door Access Groups.
- Assign authorized employees or departments to the Access Group.

### 3. Multi-Group Unlock Combinations:
- For high-security environments (cash vaults, server rooms, laboratories):
- Configure combinations requiring two or more individuals from separate Access Groups to punch within a configurable time interval (e.g., 10 seconds) before the door latch releases.

---

## 11. Live Transactions & Attendance Logs

Audit all punch activity across the enterprise.

![Transactions & Live Punches](Transactions.png)

### Transaction Features:
1. **Real-time Punch Stream:** Every terminal verification is recorded instantly with timestamp, employee badge, terminal name, and verification mode (Fingerprint, Face, Card, or Password).
2. **Paired In/Out State Resolution:** The system automatically pairs punches throughout the day into Check-In and Check-Out intervals based on the employee's assigned timetable.
3. **Manual Attendance Adjustments:**
   - Supervisors can insert missing punches for off-site assignments or forgotten badges.
   - All manual edits require an administrative reason and are logged in the audit trail.
4. **Advanced Filtering:** Filter transactions by date range, department, specific terminal, or punch state.

---

## 12. Leaves & Nepali Bikram Sambat (BS) Calendar Integration

Manage holidays and employee leave quotas with native dual-calendar support.

![Leaves & Nepali Calendar](Leaves%20and%20Holiday%20-%20Nep%20calender.png)

### Key Capabilities:
1. **Native Bikram Sambat (BS) Support:**
   - Supports Nepali years 2081, 2082, and 2083 BS alongside English Gregorian AD dates.
   - Built-in date picker provides bidirectional conversion between BS and AD dates.
2. **Public Holiday Repository:**
   - Pre-populate official national gazetted holidays and institutional holidays.
   - Scheduled holidays are automatically factored into attendance calculations, preventing false absent records.
3. **Leave Types & Quotas:**
   - Configure leave categories: Annual/Paid Leave, Sick Leave, Casual Leave, Maternity Leave, and Unpaid Leave.
   - Assign annual quota balances per employee or department.
4. **Leave Applications & Approvals:**
   - Submit leave applications with start and end dates.
   - Managers review and approve/reject applications with live balance deductions.

---

## 13. Attendance Reports & Data Export

Generate comprehensive analytical reports for payroll processing and executive reviews.

![Attendance Reports](Reports.png)

### Available Report Types:
1. **Daily Attendance Summary:** Day-by-day attendance report showing On-Time, Late, Early Exit, On Leave, and Absent personnel.
2. **Monthly Payroll Timesheet:** Complete monthly grid showing daily punch times, total regular hours worked, overtime hours, late deductions, and leave days.
3. **Exception & Audit Log:** Highlights irregular attendance patterns, repeated late arrivals, and missing punch pairs.
4. **Export Capabilities:**
   - **Export to PDF:** Generates publication-quality, formatted PDF documents ready for printing or executive distribution.
   - **Export to Excel / CSV:** Generates structured spreadsheets formatted for immediate ingestion into payroll and ERP systems.

---

## 14. System Settings, Database Management & Backup/Restore

Configure system-wide parameters, database connections, and disaster recovery.

![System Settings & Database](Settings.png)

### Administrative Options:
1. **System Preferences:** Configure company branding, session timeouts, and default calendar display (BS or AD).
2. **PostgreSQL Database Settings:** Verify database host, port (default `5432`), user credentials, and active connection pool status.
3. **Automated Scheduled Backups:** Configure daily or weekly automated database dumps stored with timestamps.
4. **One-Click Backup & Restore:**
   - Click **Create Backup** to generate an immediate `.psql` database snapshot.
   - Use **Restore Backup** in the desktop Service Controller to restore system state from any backup file.

---

## 15. Troubleshooting & Administrator FAQ

### Q1: The Server Dashboard shows "Install Service" or "Fix Services" — what should I do?
- Click **⚙ Fix Services**. Confirm the warning popup. The Service Controller will stop orphaned processes, cleanly reinstall both Windows services (`E-Attendance-API` and `E-Attendance-Web`), refresh firewall rules, and start all services fresh.

### Q2: A biometric device shows "Offline" in the Dashboard.
1. Verify the device's physical ethernet cable and power supply.
2. Ping the device IP from the server command line: `ping 192.168.1.201`.
3. Verify that the device port (default `4370`) is open and not blocked by local network switches.
4. In **Devices**, click **Test Connection**.

### Q3: How do I change the Web or API port if there is a port conflict?
- Open the desktop **Service Controller**.
- Modify the API port (default: `8081`) or Web UI port (default: `8001`).
- Click **Save Ports**. The firewall rules and configuration files are updated automatically.

### Q4: Are Nepali Bikram Sambat (BS) holidays calculated automatically?
- Yes. When holidays are entered in the **Leaves and Holiday** module using either BS or AD dates, the system automatically excludes those dates from absence calculations.
