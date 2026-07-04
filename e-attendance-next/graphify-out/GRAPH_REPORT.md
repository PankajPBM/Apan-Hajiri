# Graph Report - D:\Pankaj\Project\E-Attendance\pb-attendance-next  (2026-05-28)

## Corpus Check
- 53 files · ~141,045 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 548 nodes · 996 edges · 39 communities detected
- Extraction: 92% EXTRACTED · 8% INFERRED · 0% AMBIGUOUS · INFERRED: 79 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Community Hubs (Navigation)
- [[_COMMUNITY_Community 0|Community 0]]
- [[_COMMUNITY_Community 1|Community 1]]
- [[_COMMUNITY_Community 2|Community 2]]
- [[_COMMUNITY_Community 3|Community 3]]
- [[_COMMUNITY_Community 4|Community 4]]
- [[_COMMUNITY_Community 5|Community 5]]
- [[_COMMUNITY_Community 6|Community 6]]
- [[_COMMUNITY_Community 7|Community 7]]
- [[_COMMUNITY_Community 8|Community 8]]
- [[_COMMUNITY_Community 9|Community 9]]
- [[_COMMUNITY_Community 10|Community 10]]
- [[_COMMUNITY_Community 11|Community 11]]
- [[_COMMUNITY_Community 12|Community 12]]
- [[_COMMUNITY_Community 13|Community 13]]
- [[_COMMUNITY_Community 14|Community 14]]
- [[_COMMUNITY_Community 15|Community 15]]
- [[_COMMUNITY_Community 16|Community 16]]
- [[_COMMUNITY_Community 17|Community 17]]
- [[_COMMUNITY_Community 18|Community 18]]
- [[_COMMUNITY_Community 19|Community 19]]
- [[_COMMUNITY_Community 20|Community 20]]
- [[_COMMUNITY_Community 21|Community 21]]
- [[_COMMUNITY_Community 22|Community 22]]
- [[_COMMUNITY_Community 23|Community 23]]
- [[_COMMUNITY_Community 24|Community 24]]
- [[_COMMUNITY_Community 25|Community 25]]
- [[_COMMUNITY_Community 26|Community 26]]
- [[_COMMUNITY_Community 27|Community 27]]
- [[_COMMUNITY_Community 28|Community 28]]
- [[_COMMUNITY_Community 29|Community 29]]
- [[_COMMUNITY_Community 30|Community 30]]
- [[_COMMUNITY_Community 31|Community 31]]
- [[_COMMUNITY_Community 32|Community 32]]
- [[_COMMUNITY_Community 33|Community 33]]
- [[_COMMUNITY_Community 34|Community 34]]
- [[_COMMUNITY_Community 35|Community 35]]
- [[_COMMUNITY_Community 36|Community 36]]
- [[_COMMUNITY_Community 37|Community 37]]
- [[_COMMUNITY_Community 38|Community 38]]

## God Nodes (most connected - your core abstractions)
1. `requestJson()` - 26 edges
2. `asString()` - 16 edges
3. `resolveApiBases()` - 16 edges
4. `hasWindow()` - 15 edges
5. `asBool()` - 12 edges
6. `readShiftCalendarSettings()` - 11 edges
7. `cacheShiftConfigSnapshot()` - 11 edges
8. `toInputDate()` - 10 edges
9. `readAccessControlSnapshot()` - 10 edges
10. `normalizeShiftPlanRow()` - 10 edges

## Surprising Connections (you probably didn't know these)
- `refresh()` --calls--> `readAccessControlSnapshot()`  [INFERRED]
  D:\Pankaj\Project\E-Attendance\pb-attendance-next\app\(dashboard)\access-control\page.tsx → D:\Pankaj\Project\E-Attendance\pb-attendance-next\components\data\access-control-store.ts
- `loadStatus()` --calls--> `fetchLicenseStatus()`  [INFERRED]
  D:\Pankaj\Project\E-Attendance\pb-attendance-next\app\(auth)\login\page.tsx → D:\Pankaj\Project\E-Attendance\pb-attendance-next\components\data\license-client.ts
- `handleRefreshLicense()` --calls--> `fetchLicenseStatus()`  [INFERRED]
  D:\Pankaj\Project\E-Attendance\pb-attendance-next\app\(auth)\login\page.tsx → D:\Pankaj\Project\E-Attendance\pb-attendance-next\components\data\license-client.ts
- `handleActivateLicense()` --calls--> `activateLicenseFiles()`  [INFERRED]
  D:\Pankaj\Project\E-Attendance\pb-attendance-next\app\(auth)\login\page.tsx → D:\Pankaj\Project\E-Attendance\pb-attendance-next\components\data\license-client.ts
- `getApiBases()` --calls--> `resolveApiBases()`  [INFERRED]
  D:\Pankaj\Project\E-Attendance\pb-attendance-next\app\(dashboard)\employees\page.tsx → D:\Pankaj\Project\E-Attendance\pb-attendance-next\lib\api-bases.ts

## Communities

### Community 0 - "Community 0"
Cohesion: 0.04
Nodes (67): applyActiveState(), applyAddFormDepartmentSelection(), applyDepartmentTransfer(), applyRemoteToggle(), applyTransferDepartmentSelection(), applyVerifyMode(), buildAssignmentsFromCodes(), buildCsv() (+59 more)

### Community 1 - "Community 1"
Cohesion: 0.07
Nodes (68): syncHolidays(), resolveAssignedSchedule(), syncShiftState(), asBool(), asInt(), asString(), cacheShiftConfigSnapshot(), clampNonNegative() (+60 more)

### Community 2 - "Community 2"
Cohesion: 0.06
Nodes (34): adToBs(), bsToAd(), daysInBsMonth(), formatBs(), fromAdString(), toAdString(), resolveCalendarYearRange(), formatCalendarDate() (+26 more)

### Community 3 - "Community 3"
Cohesion: 0.08
Nodes (37): captureLatestCommandReply(), chooseDiscoveredDevice(), clearPending(), close(), connectDisconnect(), deleteDevice(), executeClearPending(), executeControlAction() (+29 more)

### Community 4 - "Community 4"
Cohesion: 0.07
Nodes (18): applySelectedColumns(), buildColumnWidths(), csvEscape(), defaultVisibleColumns(), escHtml(), exportCsv(), exportExcel(), exportPdf() (+10 more)

### Community 5 - "Community 5"
Cohesion: 0.13
Nodes (28): createDefaultAdminUser(), createDefaultFeatureAccess(), createEmptyUserForm(), createFullFeatureAccess(), generateSecurityAudit(), handleDeleteUser(), handleEditUser(), handleResetSection() (+20 more)

### Community 6 - "Community 6"
Cohesion: 0.13
Nodes (19): activateLicenseFiles(), apiBases(), asBoolean(), asNumber(), asText(), deactivateLicenseFiles(), fetchLicenseStatus(), normalizeLicenseStatus() (+11 more)

### Community 7 - "Community 7"
Cohesion: 0.1
Nodes (15): accessControlRequest(), normalizeHttpOrigin(), preferredPortsForCurrentOrigin(), rehostLoopbackBase(), resolveApiBases(), trimBase(), createDayRules(), createId() (+7 more)

### Community 8 - "Community 8"
Cohesion: 0.15
Nodes (21): authRequest(), clearAuthSession(), createAuthApiError(), createDefaultFeatureAccess(), createFullFeatureAccess(), fallbackAdminUser(), firstAllowedRoute(), firstAllowedRouteForSession() (+13 more)

### Community 9 - "Community 9"
Cohesion: 0.13
Nodes (12): applyShiftConfig(), breakBlank(), shiftGroupBlank(), shiftPlanBlank(), shiftPlanToForm(), slug(), sundayOfWeek(), TimeField() (+4 more)

### Community 10 - "Community 10"
Cohesion: 0.14
Nodes (7): buildLogAlert(), loadAlertFeed(), normalizeCategory(), normalizeLevel(), requestAlertJson(), loadAlerts(), loadAlerts()

### Community 11 - "Community 11"
Cohesion: 0.27
Nodes (13): defaultAccessControlSnapshot(), hasWindow(), loadAccessControlFromApi(), normalizeCombinations(), normalizeDay(), normalizeGroups(), normalizePeriods(), normalizePrivileges() (+5 more)

### Community 12 - "Community 12"
Cohesion: 0.13
Nodes (2): timeWithinWindow(), validateTimetable()

### Community 13 - "Community 13"
Cohesion: 0.23
Nodes (12): applyBrandingSettings(), extractBrandingSettings(), normalizeBranding(), normalizeColorValue(), normalizeLegacyLogoUrl(), normalizeLegacySupportEmail(), pickForegroundColor(), readBrandingSettings() (+4 more)

### Community 14 - "Community 14"
Cohesion: 0.25
Nodes (0): 

### Community 15 - "Community 15"
Cohesion: 1.0
Nodes (0): 

### Community 16 - "Community 16"
Cohesion: 1.0
Nodes (0): 

### Community 17 - "Community 17"
Cohesion: 1.0
Nodes (0): 

### Community 18 - "Community 18"
Cohesion: 1.0
Nodes (0): 

### Community 19 - "Community 19"
Cohesion: 1.0
Nodes (0): 

### Community 20 - "Community 20"
Cohesion: 1.0
Nodes (0): 

### Community 21 - "Community 21"
Cohesion: 1.0
Nodes (0): 

### Community 22 - "Community 22"
Cohesion: 1.0
Nodes (0): 

### Community 23 - "Community 23"
Cohesion: 1.0
Nodes (0): 

### Community 24 - "Community 24"
Cohesion: 1.0
Nodes (0): 

### Community 25 - "Community 25"
Cohesion: 1.0
Nodes (0): 

### Community 26 - "Community 26"
Cohesion: 1.0
Nodes (0): 

### Community 27 - "Community 27"
Cohesion: 1.0
Nodes (0): 

### Community 28 - "Community 28"
Cohesion: 1.0
Nodes (0): 

### Community 29 - "Community 29"
Cohesion: 1.0
Nodes (0): 

### Community 30 - "Community 30"
Cohesion: 1.0
Nodes (0): 

### Community 31 - "Community 31"
Cohesion: 1.0
Nodes (0): 

### Community 32 - "Community 32"
Cohesion: 1.0
Nodes (0): 

### Community 33 - "Community 33"
Cohesion: 1.0
Nodes (0): 

### Community 34 - "Community 34"
Cohesion: 1.0
Nodes (0): 

### Community 35 - "Community 35"
Cohesion: 1.0
Nodes (0): 

### Community 36 - "Community 36"
Cohesion: 1.0
Nodes (0): 

### Community 37 - "Community 37"
Cohesion: 1.0
Nodes (0): 

### Community 38 - "Community 38"
Cohesion: 1.0
Nodes (0): 

## Knowledge Gaps
- **Thin community `Community 15`** (2 nodes): `layout.tsx`, `RootLayout()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 16`** (2 nodes): `layout.tsx`, `DashboardLayout()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 17`** (2 nodes): `page.tsx`, `AccessControlCombinationPage()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 18`** (2 nodes): `page.tsx`, `AccessControlGroupsPage()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 19`** (2 nodes): `page.tsx`, `AccessControlHolidayPage()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 20`** (2 nodes): `page.tsx`, `AccessControlPrivilegePage()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 21`** (2 nodes): `page.tsx`, `AccessControlTimePeriodPage()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 22`** (2 nodes): `AttendanceChart()`, `attendance-chart.tsx`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 23`** (2 nodes): `department-chart.tsx`, `DepartmentChart()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 24`** (2 nodes): `device-status.tsx`, `DeviceStatus()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 25`** (2 nodes): `module-shell.tsx`, `ModuleShell()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 26`** (2 nodes): `quick-actions.tsx`, `QuickActions()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 27`** (2 nodes): `stat-cards.tsx`, `StatCards()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 28`** (2 nodes): `today-summary.tsx`, `TodaySummary()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 29`** (2 nodes): `CalendarProvider()`, `calendar-context.tsx`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 30`** (1 nodes): `next-env.d.ts`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 31`** (1 nodes): `next.config.mjs`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 32`** (1 nodes): `postcss.config.mjs`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 33`** (1 nodes): `page.tsx`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 34`** (1 nodes): `page.tsx`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 35`** (1 nodes): `page.tsx`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 36`** (1 nodes): `recent-activity.tsx`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 37`** (1 nodes): `mock-data.ts`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 38`** (1 nodes): `dev.mjs`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `resolveApiBases()` connect `Community 7` to `Community 2`, `Community 3`, `Community 5`, `Community 6`, `Community 8`, `Community 10`?**
  _High betweenness centrality (0.479) - this node is a cross-community bridge._
- **Why does `requestJson()` connect `Community 3` to `Community 0`, `Community 2`, `Community 4`, `Community 7`, `Community 9`, `Community 10`?**
  _High betweenness centrality (0.311) - this node is a cross-community bridge._
- **Why does `shiftRequest()` connect `Community 7` to `Community 1`?**
  _High betweenness centrality (0.166) - this node is a cross-community bridge._
- **Are the 12 inferred relationships involving `resolveApiBases()` (e.g. with `getApiBases()` and `fetchJson()`) actually correct?**
  _`resolveApiBases()` has 12 INFERRED edges - model-reasoned connections that need verification._
- **Should `Community 0` be split into smaller, more focused modules?**
  _Cohesion score 0.04 - nodes in this community are weakly interconnected._
- **Should `Community 1` be split into smaller, more focused modules?**
  _Cohesion score 0.07 - nodes in this community are weakly interconnected._
- **Should `Community 2` be split into smaller, more focused modules?**
  _Cohesion score 0.06 - nodes in this community are weakly interconnected._