Offline BS calendar extension:

1) Place `bs-calendar-map.json` in either:
   - same folder as PB.Attendance.exe
   - %LOCALAPPDATA%\PBAttendance\bs-calendar-map.json

2) JSON format:
{
  "2000": [30,32,31,32,31,30,30,30,29,30,29,31],
  "2001": [31,31,32,31,31,31,30,29,30,29,30,30]
}

3) Each year must have exactly 12 integers (month days), each between 28 and 32.
4) The map must include year 2000 and can extend to 3000 or beyond.
5) App runs fully offline using this file.

Important:
- Without additional official year data, embedded fallback supports only 2000-2090 BS.

