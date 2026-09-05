# -*- coding: utf-8 -*-
import os
import json

TARGET_DIR = r"d:\Pankaj\Project\E-Attendance\manual_assets"

scenes = [
    {
        "scene": 1,
        "title": "Introduction & Brand Hook",
        "duration_sec": 45,
        "start": "00:00:00,000",
        "end": "00:00:45,000",
        "image": "Login Dashboard.png",
        "narration": "Welcome to Apan Hajiri — the enterprise-grade, high-performance attendance and access control management ecosystem designed specifically for institutions, corporations, educational establishments, and industrial enterprises. Powered by an ultra-fast PostgreSQL database engine and built with seamless hardware integration for biometric and facial recognition devices, Apan Hajiri eliminates ghost attendance, simplifies complex multi-shift rosters, and introduces native dual calendar synchronization for both the Western Gregorian and Nepali Bikram Sambat calendars. In this comprehensive walkthrough, we will demonstrate how Apan Hajiri transforms your workforce operations from the ground up."
    },
    {
        "scene": 2,
        "title": "The Command Center — Real-Time Dashboard",
        "duration_sec": 55,
        "start": "00:00:45,000",
        "end": "00:01:40,000",
        "image": "Dashboard.png",
        "narration": "Upon entering the platform, administrators are greeted by the centralized Command Center. The Dashboard provides instant situational awareness across your entire organization: First, dynamic KPI cards present today's attendance numbers in real time — tallying staff members who are on time, those utilizing grace periods, employees on approved leave, and unexcused absences. Second, the live Device Health Monitor continuously tracks every biometric terminal across your network. You can instantly observe connection latency, IP addresses, and operational states, ensuring you are notified the moment any terminal requires attention. Third, intuitive analytical charts visualize department-level trends, allowing HR managers to spot attendance patterns and staffing shortages at a single glance."
    },
    {
        "scene": 3,
        "title": "Organizational Setup & Area Mapping",
        "duration_sec": 50,
        "start": "00:01:40,000",
        "end": "00:02:30,000",
        "image": "Setup.png",
        "narration": "Before enrolling staff, Apan Hajiri empowers you to construct a precise digital twin of your organization. Inside the Setup module, you have complete control over: Company and branch identities, allowing multiple locations to operate within a unified database. Multi-tier Department structures with specialized sub-departments for laser-accurate reporting. Job designations and positions that streamline permission assignments. And physical Area mapping, which links individual doors, turnstiles, and gates to specific biometric terminals. This flexible architecture guarantees that whether you operate a single retail outlet or a distributed multi-city campus, your organizational policies are enforced automatically."
    },
    {
        "scene": 4,
        "title": "Biometric Hardware & Device Communication",
        "duration_sec": 65,
        "start": "00:02:30,000",
        "end": "00:03:35,000",
        "image": "Devices.png",
        "narration": "The operational backbone of Apan Hajiri is its industrial-grade Device Management engine. Designed for high-throughput biometric terminals, facial recognition scanners, and RFID card readers, the system supports both direct TCP/IP polling and instant Cloud PUSH protocols. Key engineering highlights include: Native 13-character safe command identifiers, preventing buffer overruns and guaranteeing flawless compatibility with both modern and legacy ZKTeco firmware. One-click bidirectional synchronization: push employee names, card numbers, fingerprint templates, and facial vectors straight to the terminals, or pull transactions on demand. And complete remote terminal maintenance: synchronize internal clocks with server time, reboot hardware, and clear memory buffers without stepping away from your desk."
    },
    {
        "scene": 5,
        "title": "Workforce Directory & Biometric Profiles",
        "duration_sec": 55,
        "start": "00:03:35,000",
        "end": "00:04:30,000",
        "image": "Employees.png",
        "narration": "Managing personnel records in Apan Hajiri is fast, secure, and intuitive. Each comprehensive employee profile maintains: Personal credentials, employee badge ID, and device enrollment PIN. Assigned department, reporting hierarchy, and default shift assignment. And biometric enrollment tracking — verifying at a glance whether the employee has registered their RFID card, fingerprint templates, or facial recognition profile. Onboarding an entire workforce is effortless. Using the built-in Excel bulk import tool, hundreds of personnel records can be ingested, validated, and mapped into the system within seconds."
    },
    {
        "scene": 6,
        "title": "Shifts, Timetables, Breaks & Grace Periods",
        "duration_sec": 65,
        "start": "00:04:30,000",
        "end": "00:05:35,000",
        "image": "Shift and Timetable.png",
        "narration": "Accurate payroll and fair attendance require scheduling flexibility. Apan Hajiri's Shift engine accommodates even the most intricate operational demands. Within this module, administrators configure: First, granular Timetables: define check-in and check-out windows, allowable late grace periods in minutes, early exit tolerances, and minimum work hours required for half-day credit. Second, sophisticated Break Management: configure lunch and tea breaks as either automatically deducted intervals or punch-validated periods. And third, versatile Shift patterns: create Standard Day shifts, overnight shifts that cross the midnight threshold, weekend rotations, or rotational factory schedules without calculation discrepancies."
    },
    {
        "scene": 7,
        "title": "Schedule Assignment & Shift Rosters",
        "duration_sec": 50,
        "start": "00:05:35,000",
        "end": "00:06:25,000",
        "image": "Shift and Timetable.png",
        "narration": "Once your timetables are established, assigning them to your staff is seamless. Apan Hajiri allows flexible roster mapping: Assign master default shifts across entire branches or departments in one click. Create specialized shift groups for weekend, security, or night-shift teams. Or assign custom shift schedules to individual personnel with defined start and end dates. Need to accommodate a temporary shift swap or emergency roster change? Adjustments can be made immediately without disrupting recurring schedule templates."
    },
    {
        "scene": 8,
        "title": "Access Control, Time Periods & Multi-Group Unlock",
        "duration_sec": 60,
        "start": "00:06:25,000",
        "end": "00:07:25,000",
        "image": "Acess Control - Time Period.png",
        "narration": "Beyond time tracking, Apan Hajiri functions as a robust physical security command center. The Access Control system delivers enterprise-grade perimeter security: First, Time Periods: configure up to 50 weekly schedules down to the minute, determining exactly when doors are unlocked, when card access is permitted, and when doors remain secured. Second, Access Groups: bind time periods to specific doors and assign them to authorized personnel groups. And third, Multi-Group Unlock Combinations: for high-security environments like server rooms, vaults, or labs, require dual-custody verification — where two or more authorized individuals from distinct groups must present their credentials within a set timeframe before access is granted."
    },
    {
        "scene": 9,
        "title": "Live Punch Transactions & Paired In/Out Processing",
        "duration_sec": 55,
        "start": "00:07:25,000",
        "end": "00:08:20,000",
        "image": "Transactions.png",
        "narration": "The Transactions module provides instant transparency into every physical event occurring across your enterprise. Every punch is cataloged with full contextual data: employee name, device location, exact timestamp, and authentication method. Apan Hajiri's intelligent processing engine automatically resolves multiple punches throughout the day, pairing morning check-ins and evening check-outs against the employee's assigned timetable. Should a staff member forget their badge or attend an off-site client meeting, supervisors can insert manual attendance entries complete with mandatory audit remarks for absolute compliance."
    },
    {
        "scene": 10,
        "title": "Leaves, Holidays & Nepali Bikram Sambat (BS) Calendar",
        "duration_sec": 60,
        "start": "00:08:20,000",
        "end": "00:09:20,000",
        "image": "Leaves and Holiday - Nep calender.png",
        "narration": "A defining capability of Apan Hajiri is its native, bidirectional dual calendar engine. Built specifically to serve organizations operating in Nepal and international environments, the system features full support for the Nepali Bikram Sambat calendar alongside the Western Gregorian calendar. Administrators can configure: Official gazetted and national public holidays across Bikram Sambat years 2081, 2082, and 2083, ensuring holidays are never marked as unexcused absences. Custom leave quotas, including paid annual, sick, casual, maternity, and unpaid leaves. And comprehensive leave application workflows, allowing managers to approve or reject leave requests with real-time balance calculations."
    },
    {
        "scene": 11,
        "title": "Enterprise Reports, Analytics & Data Export",
        "duration_sec": 55,
        "start": "00:09:20,000",
        "end": "00:10:15,000",
        "image": "Reports.png",
        "narration": "Transform raw punch records into actionable payroll intelligence with Apan Hajiri's comprehensive Reporting suite. With a few clicks, generate: Daily Attendance Summaries displaying punctuality across departments. Detailed Monthly Timesheets itemizing total regular hours, overtime, weekend shifts, and late deductions. And exception audit logs highlighting anomalies before payroll processing. All reports can be filtered by date range, branch, or designation, and exported instantly into beautifully styled PDF documents or structured Excel spreadsheets ready for direct payroll integration."
    },
    {
        "scene": 12,
        "title": "Desktop Service Controller, Fix Services & Backup Management",
        "duration_sec": 50,
        "start": "00:10:15,000",
        "end": "00:11:05,000",
        "image": "Settings.png",
        "narration": "Behind the web application sits the Apan Hajiri Service Controller — a dedicated Windows desktop application ensuring flawless system uptime. From this administrative dashboard, you can: Start, stop, and monitor the core API and Web services with a single master power toggle. Modify network ports with automatic Windows Firewall configuration. Execute one-click automated database backups or restore from existing snapshots. And utilize the intelligent 'Fix Services' utility: a guarded recovery tool that terminates orphaned processes, reinstalls Windows background services with automatic recovery policies, and restarts the environment cleanly in case of system reboots or network changes."
    },
    {
        "scene": 13,
        "title": "Summary, Enterprise Support & Call to Action",
        "duration_sec": 25,
        "start": "00:11:05,000",
        "end": "00:11:30,000",
        "image": "Login Dashboard.png",
        "narration": "Streamline your workforce management, secure your facilities, and eliminate payroll headaches today with Apan Hajiri. Whether you are deploying for a single corporate office or a nationwide institution, our engineering team is here to support your transition. Contact us today to schedule your live demonstration or deployment. Thank you for watching!"
    }
]

# Write SRT Subtitle File
srt_path = os.path.join(TARGET_DIR, "apan_hajiri_video_subtitles.srt")
with open(srt_path, "w", encoding="utf-8") as f:
    for idx, s in enumerate(scenes, 1):
        f.write(f"{idx}\n")
        f.write(f"{s['start']} --> {s['end']}\n")
        f.write(f"{s['narration']}\n\n")

print(f"Generated subtitles: {srt_path}")

# Write Storyboard Manifest JSON
manifest_path = os.path.join(TARGET_DIR, "storyboard_manifest.json")
with open(manifest_path, "w", encoding="utf-8") as f:
    json.dump({
        "project": "Apan Hajiri (E-Attendance)",
        "version": "1.0",
        "total_duration_sec": 690,
        "total_duration_formatted": "11:30",
        "scenes": scenes
    }, f, indent=2)

print(f"Generated manifest: {manifest_path}")

# Write helper script inside manual_assets
helper_script_path = os.path.join(TARGET_DIR, "generate_video_assets.py")
with open(helper_script_path, "w", encoding="utf-8") as f:
    f.write(open(__file__, "r", encoding="utf-8").read())

print(f"Saved generator script: {helper_script_path}")
