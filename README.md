# E-Attendance

A self-hosted biometric attendance management system for ZKTeco devices (fingerprint, face, and card), with a web dashboard for employee/device management, shift scheduling, leave management, and reporting.

See **[ARCHITECTURE.md](ARCHITECTURE.md)** for a full breakdown of how the system is built and how its pieces fit together, **[CHANGELOG.md](CHANGELOG.md)** for release history and the versioning scheme, **[DEVICE_COMMAND_FIXES.md](DEVICE_COMMAND_FIXES.md)** for the running log of device-protocol quirks discovered in the field, and **[TROUBLESHOOTING.md](TROUBLESHOOTING.md)** for known install/runtime issues with exact diagnosis steps and fixes.

## Tech stack

- **Backend:** .NET 10, ASP.NET Core minimal APIs, running as a Windows Service
- **Database:** PostgreSQL (bundled and installed alongside the app)
- **Frontend:** Next.js (App Router), built as a static export and served by the backend
- **Device protocol:** ZKTeco PUSH SDK (HTTP-based; devices poll the server)

## Project structure

```
E.Attendance.Core/        Domain models and interfaces
E.Attendance.Data/        Data access (raw ADO.NET + EF Core)
E.Attendance.Services/    Business logic and device-protocol services
E.Attendance.Web/         The application itself (Program.cs, API endpoints, wwwroot)
E.Attendance/             WinForms installer/service-control utility
e-attendance-next/        Next.js frontend (static export)
installer/                Inno Setup installer script and protocol reference docs
```

## Building

**Backend:**
```
dotnet build E.Attendance.Web/E.Attendance.Web.csproj -c Debug
```
The build automatically runs `npm install && npm run build` for the frontend and copies the static export into `E.Attendance.Web/wwwroot`.

**Frontend only:**
```
cd e-attendance-next
npm install
npm run build
```

Release builds and installer packaging are done in Visual Studio.

## Requirements

- .NET 10 SDK
- Node.js (for the frontend build)
- PostgreSQL (bundled with the installer for end users; a local instance is needed for development)
