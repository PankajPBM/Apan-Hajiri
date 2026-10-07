#!/usr/bin/env bash
# ==============================================================================
# Apan Hajiri - Automated Linux Installer
# GitHub: https://github.com/PankajPBM/Apan-Hajiri
#
# Usage for Private Repo:
#   GITHUB_TOKEN=your_pat curl -sSL -H "Authorization: token your_pat" \
#     https://raw.githubusercontent.com/PankajPBM/Apan-Hajiri/main/install-linux.sh | sudo -E bash
#
# Usage from cloned repo:
#   git clone https://github.com/PankajPBM/Apan-Hajiri.git
#   cd Apan-Hajiri && sudo ./install-linux.sh

set -euo pipefail

# Visual colors
RED='\033[0;31m'
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
BOLD='\033[1m'
NC='\033[0m' # No Color

INSTALL_DIR="/opt/apan-hajiri"
SERVICE_NAME="apan-hajiri"
DB_NAME="E_Attendace"
DB_USER="E_Attendace"
DB_PORT="${DB_PORT:-5432}"
WEB_PORT="${WEB_PORT:-8001}"
API_PORT="${API_PORT:-8081}"
REPO_URL="https://github.com/PankajPBM/Apan-Hajiri.git"
BRANCH="${BRANCH:-main}"

# Generate random secure password if not provided
if [ -z "${DB_PASSWORD:-}" ]; then
  DB_PASSWORD="AH_$(tr -dc A-Za-z0-9 </dev/urandom 2>/dev/null | head -c 16 || openssl rand -hex 8)"
fi

echo -e "${CYAN}${BOLD}"
echo "======================================================================"
echo "          Apan Hajiri - Automated Linux Installer (v1.1.16)           "
echo "======================================================================"
echo -e "${NC}"

# Check for root privilege
if [ "$EUID" -ne 0 ]; then
  echo -e "${RED}[ERROR] Please run this script as root or with sudo:${NC}"
  echo "  curl -sSL https://raw.githubusercontent.com/PankajPBM/Apan-Hajiri/main/install-linux.sh | sudo bash"
  exit 1
fi

# Detect OS
if [ -f /etc/os-release ]; then
  . /etc/os-release
  OS_ID=$ID
  OS_VERSION_ID=${VERSION_ID:-}
else
  echo -e "${RED}[ERROR] Unsupported Linux distribution. Could not find /etc/os-release.${NC}"
  exit 1
fi

echo -e "${BLUE}[1/7] Detected OS:${NC} ${PRETTY_NAME:-$OS_ID}"

# Function: Install dependencies on Debian/Ubuntu
install_debian_deps() {
  export DEBIAN_FRONTEND=noninteractive
  echo -e "${BLUE}[2/7] Updating package index and installing base dependencies...${NC}"
  apt-get update -y
  apt-get install -y --no-install-recommends \
    curl \
    wget \
    git \
    gnupg \
    ca-certificates \
    lsb-release \
    postgresql \
    postgresql-contrib \
    ufw \
    tar

  # Ensure Node.js >= 20 LTS is installed (Next.js requires >= 20.9.0)
  NODE_MAJOR=0
  if command -v node >/dev/null 2>&1; then
    NODE_MAJOR=$(node -v 2>/dev/null | tr -d 'v' | cut -d'.' -f1 || echo 0)
  fi

  if [ "$NODE_MAJOR" -lt 20 ]; then
    echo -e "${BLUE}[INFO] Node.js 20+ required (detected v${NODE_MAJOR:-none}). Upgrading to Node.js 20 LTS...${NC}"
    mkdir -p /etc/apt/keyrings
    curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg --yes
    echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_20.x nodistro main" | tee /etc/apt/sources.list.d/nodesource.list
    apt-get update -y
    apt-get install -y nodejs
  fi

  # Ensure .NET 10 SDK is installed and active
  export DOTNET_ROOT="/usr/share/dotnet"
  export PATH="/usr/share/dotnet:$PATH"
  if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q "^10\."; then
    echo -e "${BLUE}[INFO] Installing Microsoft .NET 10 SDK via dotnet-install.sh...${NC}"
    mkdir -p /usr/share/dotnet
    wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
    chmod +x /tmp/dotnet-install.sh
    /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet
    ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
    rm -f /tmp/dotnet-install.sh
  fi
}

# Function: Install dependencies on RHEL/Fedora/Rocky/AlmaLinux
install_rhel_deps() {
  echo -e "${BLUE}[2/7] Installing base dependencies on RHEL/CentOS/Rocky...${NC}"
  dnf install -y curl wget git tar postgresql-server postgresql-contrib

  # Init Postgres if not initialized
  if [ ! -f /var/lib/pgsql/data/PG_VERSION ]; then
    postgresql-setup --initdb || true
  fi
  systemctl enable postgresql
  systemctl start postgresql

  NODE_MAJOR=0
  if command -v node >/dev/null 2>&1; then
    NODE_MAJOR=$(node -v 2>/dev/null | tr -d 'v' | cut -d'.' -f1 || echo 0)
  fi

  if [ "$NODE_MAJOR" -lt 20 ]; then
    dnf module reset -y nodejs || true
    dnf module enable -y nodejs:20 || true
    dnf install -y nodejs
  fi

  export DOTNET_ROOT="/usr/share/dotnet"
  export PATH="/usr/share/dotnet:$PATH"
  if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q "^10\."; then
    wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
    chmod +x /tmp/dotnet-install.sh
    /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet
    ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
    rm -f /tmp/dotnet-install.sh
  fi
}

case "$OS_ID" in
  ubuntu|debian|raspbian)
    install_debian_deps
    ;;
  centos|rhel|rocky|almalinux|fedora)
    install_rhel_deps
    ;;
  *)
    echo -e "${YELLOW}[WARN] Unknown distribution '$OS_ID'. Attempting Debian-style setup...${NC}"
    install_debian_deps
    ;;
esac

# ------------------------------------------------------------------------------
# [3/7] Configure PostgreSQL Database
# ------------------------------------------------------------------------------
echo -e "${BLUE}[3/7] Setting up PostgreSQL database '${DB_NAME}'...${NC}"
systemctl enable postgresql
systemctl start postgresql

# Create database user and database if not existing
sudo -u postgres psql -tAc "SELECT 1 FROM pg_roles WHERE rolname='${DB_USER}'" | grep -q 1 || \
  sudo -u postgres psql -c "CREATE USER \"${DB_USER}\" WITH PASSWORD '${DB_PASSWORD}';"

# Always update password to match configured DB_PASSWORD
sudo -u postgres psql -c "ALTER USER \"${DB_USER}\" WITH PASSWORD '${DB_PASSWORD}';"

# Create database owned by user
sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname='${DB_NAME}'" | grep -q 1 || \
  sudo -u postgres psql -c "CREATE DATABASE \"${DB_NAME}\" OWNER \"${DB_USER}\";"

sudo -u postgres psql -c "GRANT ALL PRIVILEGES ON DATABASE \"${DB_NAME}\" TO \"${DB_USER}\";"

# Ensure PostgreSQL allows local md5/scram password connections
PG_HBA=$(sudo -u postgres psql -t -P format=unaligned -c "SHOW hba_file;")
if [ -f "$PG_HBA" ]; then
  if ! grep -q "E_Attendace" "$PG_HBA"; then
    echo "host    E_Attendace     E_Attendace     127.0.0.1/32            scram-sha-256" >> "$PG_HBA"
    echo "host    E_Attendace     E_Attendace     ::1/128                 scram-sha-256" >> "$PG_HBA"
    systemctl reload postgresql || true
  fi
fi

# ------------------------------------------------------------------------------
# [4/7] Deploy application (Pre-compiled package or source build)
# ------------------------------------------------------------------------------
echo -e "${BLUE}[4/7] Deploying Apan Hajiri application...${NC}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" 2>/dev/null && pwd || echo "")"
DEPLOYED=false
mkdir -p "$INSTALL_DIR"

# 1. Fast path: Check for local pre-compiled release package
PACKAGE_PATH=""
for candidate in \
  "$SCRIPT_DIR/releases/apan-hajiri-linux-x64.tar.gz" \
  "$SCRIPT_DIR/apan-hajiri-linux-x64.tar.gz" \
  "/tmp/apan-hajiri-linux-x64.tar.gz"; do
  if [ -f "$candidate" ]; then
    PACKAGE_PATH="$candidate"
    break
  fi
done

if [ -n "$PACKAGE_PATH" ]; then
  echo -e "${GREEN}[INFO] Found pre-compiled Linux release package: ${PACKAGE_PATH}${NC}"
  echo -e "${BLUE}[INFO] Extracting release directly to ${INSTALL_DIR}...${NC}"
  tar -xzf "$PACKAGE_PATH" -C "$INSTALL_DIR"
  DEPLOYED=true
fi

# 2. Source path: Clone / build if package is not present
if [ "$DEPLOYED" = false ]; then
  if [ -n "$SCRIPT_DIR" ] && [ -f "$SCRIPT_DIR/E.Attendance.Web/E.Attendance.Web.csproj" ]; then
    echo -e "${BLUE}[INFO] Detected local repository source at ${SCRIPT_DIR}...${NC}"
    BUILD_SRC="$SCRIPT_DIR"
    CLEANUP_BUILD_SRC=false
    if [ -d "$SCRIPT_DIR/.git" ]; then
      echo -e "${BLUE}[INFO] Resetting local repository to latest commit from origin/${BRANCH}...${NC}"
      git -C "$SCRIPT_DIR" fetch origin "$BRANCH" 2>/dev/null || true
      git -C "$SCRIPT_DIR" reset --hard "origin/$BRANCH" 2>/dev/null || true
    fi
  else
    BUILD_SRC="/tmp/apan-hajiri-build"
    CLEANUP_BUILD_SRC=true
    rm -rf "$BUILD_SRC"
    mkdir -p "$BUILD_SRC"

    if [ -n "${GITHUB_TOKEN:-}" ]; then
      CLONE_URL="https://${GITHUB_TOKEN}@github.com/PankajPBM/Apan-Hajiri.git"
    else
      CLONE_URL="$REPO_URL"
    fi

    echo -e "${BLUE}[INFO] Cloning repository...${NC}"
    if ! git clone --depth 1 --branch "$BRANCH" "$CLONE_URL" "$BUILD_SRC" 2>/tmp/git_clone.err; then
      echo -e "${YELLOW}[NOTICE] Direct clone failed — repository is private.${NC}"
      if [ -t 0 ] && [ -z "${GITHUB_TOKEN:-}" ]; then
        read -r -p "Enter GitHub Personal Access Token (PAT): " GITHUB_TOKEN || true
        if [ -n "${GITHUB_TOKEN:-}" ]; then
          CLONE_URL="https://${GITHUB_TOKEN}@github.com/PankajPBM/Apan-Hajiri.git"
          git clone --depth 1 --branch "$BRANCH" "$CLONE_URL" "$BUILD_SRC"
        fi
      else
        echo -e "${RED}[ERROR] Could not clone private repository.${NC}"
        echo "Pass your token using: GITHUB_TOKEN=your_token curl -sSL ... | sudo -E bash"
        cat /tmp/git_clone.err || true
        exit 1
      fi
    fi
  fi

  # Check if cloned source contains pre-compiled release package
  if [ -f "$BUILD_SRC/releases/apan-hajiri-linux-x64.tar.gz" ]; then
    echo -e "${GREEN}[INFO] Found bundled pre-compiled release in repository!${NC}"
    echo -e "${BLUE}[INFO] Extracting release directly to ${INSTALL_DIR}...${NC}"
    tar -xzf "$BUILD_SRC/releases/apan-hajiri-linux-x64.tar.gz" -C "$INSTALL_DIR"
    DEPLOYED=true
  else
    echo -e "${BLUE}[INFO] Cleaning build cache (bin/obj)...${NC}"
    rm -rf "$BUILD_SRC/E.Attendance.Web/bin" "$BUILD_SRC/E.Attendance.Web/obj"
    rm -rf "$BUILD_SRC/E.Attendance.Core/bin" "$BUILD_SRC/E.Attendance.Core/obj"
    rm -rf "$BUILD_SRC/E.Attendance.Data/bin" "$BUILD_SRC/E.Attendance.Data/obj"
    rm -rf "$BUILD_SRC/E.Attendance.Services/bin" "$BUILD_SRC/E.Attendance.Services/obj"

    echo -e "${BLUE}[INFO] Building Next.js static frontend...${NC}"
    cd "$BUILD_SRC/e-attendance-next"
    npm install --silent
    npm run build

    echo -e "${BLUE}[INFO] Syncing frontend export to backend wwwroot...${NC}"
    mkdir -p "$BUILD_SRC/E.Attendance.Web/wwwroot"
    rm -rf "$BUILD_SRC/E.Attendance.Web/wwwroot/*"
    cp -r "$BUILD_SRC/e-attendance-next/out/"* "$BUILD_SRC/E.Attendance.Web/wwwroot/"

    echo -e "${BLUE}[INFO] Publishing ASP.NET backend for Linux...${NC}"
    cd "$BUILD_SRC"

    export DOTNET_ROOT="/usr/share/dotnet"
    export PATH="/usr/share/dotnet:$PATH"

    dotnet publish "$BUILD_SRC/E.Attendance.Web/E.Attendance.Web.csproj" \
      -c Release \
      -p:SkipFrontendBuild=true \
      --self-contained false \
      -o "$INSTALL_DIR"
  fi

  # Clean temporary build files if cloned
  if [ "$CLEANUP_BUILD_SRC" = true ]; then
    rm -rf "$BUILD_SRC" /tmp/git_clone.err 2>/dev/null || true
  fi
fi

# ------------------------------------------------------------------------------
# [5/7] Configure appsettings.json
# ------------------------------------------------------------------------------
echo -e "${BLUE}[5/7] Configuring application settings...${NC}"
cat <<EOF > "$INSTALL_DIR/appsettings.json"
{
  "ConnectionStrings": {
    "PostgreSQLConnection": "Host=127.0.0.1;Port=${DB_PORT};Database=${DB_NAME};Username=${DB_USER};Password=${DB_PASSWORD};Search Path=${DB_NAME};Include Error Detail=True;Maximum Pool Size=100;Connection Lifetime=300"
  },
  "DatabaseSettings": {
    "UsePostgreSql": true,
    "ServiceName": "postgresql",
    "CommandTimeout": 30,
    "MaxRetryCount": 3
  },
  "Kestrel": {
    "Endpoints": {
      "WebUI": {
        "Url": "http://0.0.0.0:${WEB_PORT}"
      },
      "Api": {
        "Url": "http://0.0.0.0:${API_PORT}"
      }
    }
  },
  "DeviceSettings": {
    "DefaultTimeZone": "+5:45",
    "SyncIntervalSeconds": 60,
    "CommandQueueBatchSize": 10,
    "PollDelaySeconds": 10,
    "AllowUnknownEmployeeAutoCreate": false
  },
  "AllowedHosts": "*"
}
EOF

# Ensure runtime websettings.json exists in system common app data
APPDATA_DIR="/usr/share/E_Attendance"
mkdir -p "$APPDATA_DIR"
cat <<EOF > "$APPDATA_DIR/websettings.json"
{
  "Port": ${API_PORT},
  "FrontendPort": ${WEB_PORT}
}
EOF
chown -R apan-hajiri:apan-hajiri "$APPDATA_DIR" 2>/dev/null || true

# Create dedicated service user if not exists
if ! id -u apan-hajiri >/dev/null 2>&1; then
  useradd --system --no-create-home --shell /usr/sbin/nologin apan-hajiri || true
fi

chown -R apan-hajiri:apan-hajiri "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/E.Attendance.Web"

# Ensure schema and initial admin account exist in PostgreSQL
sudo -u postgres psql -d "${DB_NAME}" <<EOSQL || true
CREATE SCHEMA IF NOT EXISTS "${DB_NAME}";
CREATE TABLE IF NOT EXISTS "${DB_NAME}"."auth_users" (
  "Id" SERIAL PRIMARY KEY,
  "UserName" VARCHAR(50) UNIQUE NOT NULL,
  "PasswordHash" VARCHAR(64) NOT NULL,
  "FullName" VARCHAR(100),
  "Role" VARCHAR(50) DEFAULT 'Admin',
  "Status" VARCHAR(20) DEFAULT 'active',
  "FeatureAccessJson" TEXT DEFAULT '{}'
);
INSERT INTO "${DB_NAME}"."auth_users" ("UserName", "PasswordHash", "FullName", "Role", "Status", "FeatureAccessJson")
VALUES ('admin', md5('PB@TechZone'), 'Admin User', 'Admin', 'active', '{}')
ON CONFLICT ("UserName") DO NOTHING;
EOSQL

# ------------------------------------------------------------------------------
# [6/7] Create and Start systemd Service (Auto-run on boot & auto-restart)
# ------------------------------------------------------------------------------
echo -e "${BLUE}[6/7] Creating and enabling systemd auto-start service...${NC}"
cat <<EOF > "/etc/systemd/system/${SERVICE_NAME}.service"
[Unit]
Description=Apan Hajiri Web & Biometric Service
After=network.target postgresql.service
Wants=postgresql.service

[Service]
Type=simple
WorkingDirectory=${INSTALL_DIR}
ExecStart=/usr/bin/dotnet ${INSTALL_DIR}/E.Attendance.Web.dll --contentRoot ${INSTALL_DIR}
Restart=always
RestartSec=5
KillSignal=SIGINT
SyslogIdentifier=apan-hajiri
User=apan-hajiri
Group=apan-hajiri
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_ROOT=/usr/share/dotnet
Environment=PATH=/usr/share/dotnet:/usr/bin:/bin
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "${SERVICE_NAME}"
systemctl restart "${SERVICE_NAME}"

# ------------------------------------------------------------------------------
# [7/7] Firewall Configuration
# ------------------------------------------------------------------------------
echo -e "${BLUE}[7/7] Configuring firewall rules...${NC}"
if command -v ufw >/dev/null 2>&1 && ufw status | grep -q "Status: active"; then
  ufw allow "${WEB_PORT}/tcp" comment "Apan Hajiri Web UI"
  ufw allow "${API_PORT}/tcp" comment "Apan Hajiri API & ADMS"
  ufw allow 4370/udp comment "ZKTeco UDP Standalone"
  echo -e "${GREEN}[INFO] Allowed ports ${WEB_PORT}/tcp, ${API_PORT}/tcp, and 4370/udp in UFW firewall.${NC}"
fi

# Detect Server IP
SERVER_IP=$(hostname -I 2>/dev/null | awk '{print $1}' || echo "127.0.0.1")

echo -e "\n${GREEN}${BOLD}"
echo "======================================================================"
echo "          Apan Hajiri Installation Successfully Completed!            "
echo "======================================================================"
echo -e "${NC}"
echo -e "${BOLD}Access Points:${NC}"
echo -e "  Web Application: ${CYAN}http://${SERVER_IP}:${WEB_PORT}${NC}"
echo -e "  API & Devices:   ${CYAN}http://${SERVER_IP}:${API_PORT}${NC}"
echo -e "  Default Login:   ${YELLOW}admin${NC} / ${YELLOW}PB@TechZone${NC}"
echo ""
echo -e "${BOLD}Database Connection Details:${NC}"
echo -e "  Database:        ${DB_NAME}"
echo -e "  User:            ${DB_USER}"
echo -e "  Password:        ${DB_PASSWORD}"
echo -e "  Port:            ${DB_PORT}"
echo ""
echo -e "${BOLD}Service Management Commands:${NC}"
echo -e "  Check status:    ${CYAN}sudo systemctl status ${SERVICE_NAME}${NC}"
echo -e "  Restart service: ${CYAN}sudo systemctl restart ${SERVICE_NAME}${NC}"
echo -e "  View logs:       ${CYAN}sudo journalctl -u ${SERVICE_NAME} -f${NC}"
echo -e "  Config file:     ${CYAN}${INSTALL_DIR}/appsettings.json${NC}"
echo ""
echo -e "${GREEN}All tables will be automatically created on first startup.${NC}"
echo "======================================================================"
