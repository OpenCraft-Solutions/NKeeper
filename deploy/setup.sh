#!/usr/bin/env bash
# NKeeper's native installer (docs/architecture.md §8): builds NKeeper from this checkout and installs it
# with systemd, nginx and PostgreSQL, on Debian/Ubuntu (apt) or the RHEL family and Fedora (dnf).
#
#   sudo deploy/setup.sh install [options]     install, or upgrade an existing install
#   sudo deploy/setup.sh uninstall [--purge]   remove it; --purge also drops the database and /etc/nkeeper
#
# Run it with --help for the options.
set -euo pipefail

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
readonly REPO
readonly PREFIX=/usr/lib/nkeeper
readonly CONFIG=/etc/nkeeper
readonly TRUST=$CONFIG/plugin-trust
readonly TLS=$CONFIG/tls
readonly ENV_FILE=$CONFIG/nkeeper.env
readonly KEYRING=$CONFIG/data-protection.pem
readonly STATE=/var/lib/nkeeper
readonly CACHE=/var/cache/nkeeper-build
readonly SERVER_PORT=5080

# guacd, for remote desktops (docs/architecture.md §7.20, D11): built from the Apache Guacamole source release, whose
# SHA-256 is pinned here (checked against the release's signature by Apache's code-signing key when it was pinned).
readonly GUACD_VERSION=1.6.0
readonly GUACD_SHA256=8bc45675da96d7b6f39728160181e3d4ff3c08f460f6d26de5805b642bf13f2b
readonly GUACD_PREFIX=/usr/lib/nkeeper/guacd

usage() {
    cat <<'EOF'
NKeeper's native installer (docs/architecture.md §8).

  sudo deploy/setup.sh install [options]
      Installs NKeeper, or upgrades it: data, settings and accounts are kept.
        --server-name <name>    a name or address browsers use to reach it; repeatable.
                                Default: this machine's host name and IP addresses.
        --cert <file> --key <file>
                                nginx's certificate and key (PEM). Default: a self-signed one.
        --admin-user <name>     the first administrator, on a fresh install (default: admin)
        --admin-email <email>   their email address (default: admin@<host name>)
        --admin-password <pw>   their password (default: generated, and printed once)
        --no-guacd              don't build or run guacd: remote desktops then say the gateway is unavailable
        --web-domain <domain>   where web pages open: nkw-<id>.<domain> (docs/architecture.md §7.18). Needs a
                                wildcard DNS record, *.<domain>, pointing at this server. Kept on upgrade.
        --yes                   don't ask anything

  sudo deploy/setup.sh uninstall [--purge]
      Removes the service, its files and nginx's site. The database and /etc/nkeeper are kept,
      unless --purge is given.
EOF
}

bold=$'\e[1m' red=$'\e[31m' green=$'\e[32m' yellow=$'\e[33m' reset=$'\e[0m'
[ -t 1 ] || bold='' red='' green='' yellow='' reset=''
step() { echo "${bold}==> $*${reset}"; }
ok() { echo "${green}    $*${reset}"; }
warn() { echo "${yellow}warning: $*${reset}" >&2; }
die() { echo "${red}error: $*${reset}" >&2; exit 1; }

random_text() { head -c 64 /dev/urandom | od -An -tx1 | tr -d ' \n' | head -c "$1"; }

# set_env FILE NAME VALUE: sets NAME=VALUE in a systemd environment file, replacing any earlier line.
set_env() {
    local file=$1 name=$2 value=$3 tmp
    tmp=$(mktemp)
    grep -v "^$name=" "$file" >"$tmp" 2>/dev/null || true
    printf '%s=%s\n' "$name" "$value" >>"$tmp"
    cat "$tmp" >"$file"
    rm -f "$tmp"
}

# get_env FILE NAME: the value of NAME in a systemd environment file, without its quotes; empty if unset.
get_env() {
    if [ -f "$1" ]; then
        sed -n "s/^$2=//p" "$1" | tail -n 1 | sed 's/^"\(.*\)"$/\1/'
    fi
}

selinux() { command -v selinuxenabled >/dev/null 2>&1 && selinuxenabled; }

# --- Options -------------------------------------------------------------------------------------

command=${1:-}
[ $# -gt 0 ] && shift
names=() cert="" key="" admin_user=admin admin_email="" admin_password="" assume_yes=false purge=false web_domain="" no_guacd=false
while [ $# -gt 0 ]; do
    case $1 in
        --server-name) names+=("${2:?--server-name needs a value}"); shift 2 ;;
        --web-domain) web_domain=$(tr '[:upper:]' '[:lower:]' <<<"${2:?--web-domain needs a value}"); shift 2 ;;
        --cert) cert=${2:?--cert needs a file}; shift 2 ;;
        --key) key=${2:?--key needs a file}; shift 2 ;;
        --admin-user) admin_user=${2:?--admin-user needs a value}; shift 2 ;;
        --admin-email) admin_email=${2:?--admin-email needs a value}; shift 2 ;;
        --admin-password) admin_password=${2:?--admin-password needs a value}; shift 2 ;;
        --yes) assume_yes=true; shift ;;
        --no-guacd) no_guacd=true; shift ;;
        --purge) purge=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "Unknown option: $1 (see --help)" ;;
    esac
done

# --- Packages ------------------------------------------------------------------------------------

pm=""

install_packages() {
    step "Installing PostgreSQL, nginx and OpenSSL"
    if command -v apt-get >/dev/null 2>&1; then
        pm=apt
        export DEBIAN_FRONTEND=noninteractive
        apt-get update -qq
        local icu
        icu=$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{print $1}' | sort -V | tail -n 1)
        apt-get install -y -qq postgresql nginx openssl curl ca-certificates tar xz-utils ${icu:+"$icu"} >/dev/null
    elif command -v dnf >/dev/null 2>&1; then
        pm=dnf
        # No curl package: RHEL's curl-minimal, already present, conflicts with it.
        dnf install -y -q postgresql-server nginx openssl ca-certificates tar xz libicu policycoreutils-python-utils >/dev/null
    else
        die "This installer supports apt (Debian, Ubuntu) and dnf (the RHEL family, Fedora)."
    fi
    ok "Installed."
}

# --- PostgreSQL ----------------------------------------------------------------------------------

postgres_psql() { (cd / && runuser -u postgres -- psql "$@"); }

db_password=""

setup_postgres() {
    step "Setting up PostgreSQL"
    if [ "$pm" = dnf ] && [ ! -s /var/lib/pgsql/data/PG_VERSION ]; then
        postgresql-setup --initdb >/dev/null
    fi
    systemctl enable --now postgresql >/dev/null 2>&1
    local i
    for ((i = 0; i < 30; i++)); do
        postgres_psql -qtAc 'SELECT 1' >/dev/null 2>&1 && break
        sleep 1
    done
    postgres_psql -qtAc 'SELECT 1' >/dev/null 2>&1 || die "PostgreSQL didn't start (systemctl status postgresql)."

    db_password=$(get_env "$ENV_FILE" NKEEPER_DB_PASSWORD)
    db_password=${db_password:-$(random_text 48)}
    postgres_psql -q -v ON_ERROR_STOP=1 -v password="$db_password" -d postgres <<'SQL'
SET password_encryption = 'scram-sha-256';
SELECT 'CREATE ROLE nkeeper LOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'nkeeper')\gexec
ALTER ROLE nkeeper WITH LOGIN PASSWORD :'password';
SELECT 'CREATE DATABASE nkeeper OWNER nkeeper' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'nkeeper')\gexec
SQL

    # Password sign-in for NKeeper over loopback only, ahead of other rules (RHEL's default is ident).
    local hba tmp
    hba=$(postgres_psql -tAc 'SHOW hba_file')
    if ! grep -q 'NKeeper' "$hba"; then
        tmp=$(mktemp)
        { echo "# NKeeper (added by deploy/setup.sh)"
          echo "host    nkeeper    nkeeper    127.0.0.1/32    scram-sha-256"
          echo "host    nkeeper    nkeeper    ::1/128         scram-sha-256"
          cat "$hba"; } >"$tmp"
        cat "$tmp" >"$hba"
        rm -f "$tmp"
        postgres_psql -qc 'SELECT pg_reload_conf()' >/dev/null
    fi
    ok "Database nkeeper ready."
}

# --- Build ---------------------------------------------------------------------------------------

dotnet_bin="" node_bin_dir="" rid="" build_dir=""

fetch_toolchain() {
    local node_major arch
    node_major=$(tr -dc '0-9' <"$REPO/.nvmrc")
    step "Fetching the .NET 10 SDK and Node.js $node_major into $CACHE"
    mkdir -p "$CACHE"
    chmod 700 "$CACHE"
    case "$(uname -m)" in
        x86_64) arch=x64 ;;
        aarch64|arm64) arch=arm64 ;;
        *) die "Unsupported CPU architecture: $(uname -m)" ;;
    esac
    rid="linux-$arch"

    if [ ! -x "$CACHE/dotnet/dotnet" ] || ! "$CACHE/dotnet/dotnet" --list-sdks | grep -q '^10\.'; then
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$CACHE/dotnet-install.sh"
        bash "$CACHE/dotnet-install.sh" --channel 10.0 --install-dir "$CACHE/dotnet" --no-path >/dev/null
    fi
    dotnet_bin=$CACHE/dotnet/dotnet

    if [ ! -x "$CACHE/node/bin/node" ] || [[ "$("$CACHE/node/bin/node" --version)" != "v$node_major."* ]]; then
        local base="https://nodejs.org/dist/latest-v$node_major.x" file sum
        curl -fsSL "$base/SHASUMS256.txt" -o "$CACHE/node-sums.txt"
        file=$(awk -v suffix="-linux-$arch.tar.xz" '$2 ~ suffix"$" {print $2}' "$CACHE/node-sums.txt")
        [ -n "$file" ] || die "There's no Node.js $node_major download for linux-$arch."
        curl -fsSL "$base/$file" -o "$CACHE/$file"
        sum=$(awk -v f="$file" '$2 == f {print $1}' "$CACHE/node-sums.txt")
        echo "$sum  $CACHE/$file" | sha256sum -c --quiet - || die "The Node.js download failed its checksum."
        rm -rf "$CACHE/node" && mkdir -p "$CACHE/node"
        tar -xJf "$CACHE/$file" -C "$CACHE/node" --strip-components=1
        rm -f "$CACHE/$file"
    fi
    node_bin_dir=$CACHE/node/bin
    ok "Ready."
}

# The built-in plugins: every plugins/<id>/backend project, with the ID its manifest declares.
plugin_projects=() plugin_ids=()

find_plugins() {
    local manifest id
    for manifest in "$REPO"/plugins/*/backend/nkeeper-plugin.json; do
        [ -f "$manifest" ] || continue
        id=$(sed -n 's/^[[:space:]]*"id":[[:space:]]*"\([^"]*\)".*/\1/p' "$manifest" | head -n 1)
        [ -n "$id" ] || die "No plugin ID in $manifest."
        plugin_ids+=("$id")
        plugin_projects+=("$(dirname "$manifest")")
    done
}

build() {
    find_plugins
    step "Building NKeeper (a few minutes the first time)"
    # From a copy, so the checkout gets no root-owned build output.
    build_dir=$CACHE/src
    rm -rf "$build_dir" && mkdir -p "$build_dir"
    (cd "$REPO" && tar --exclude=node_modules --exclude=bin --exclude=obj --exclude=dist --exclude=.git \
        --exclude=artifacts --exclude=.dev -cf - .) | tar -xf - -C "$build_dir"

    export PATH="$CACHE/dotnet:$node_bin_dir:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
    (cd "$build_dir" && npm ci --no-audit --no-fund --loglevel=error && npm run build --silent) >/dev/null
    "$dotnet_bin" publish "$build_dir/core/backend/NKeeper.Server/NKeeper.Server.csproj" \
        -c Release -r "$rid" --self-contained -o "$build_dir/out/server" -p:DebugType=none >/dev/null

    # The built-in plugins, signed with a one-time install key (docs/plugin-security.md §6.1), whose private
    # key is deleted as soon as they're signed.
    local keys allow=() id project package
    keys=$(mktemp -d)
    chmod 700 "$keys"
    "$dotnet_bin" build "$build_dir/tools/NKeeper.PluginTool/NKeeper.PluginTool.csproj" -c Release >/dev/null
    for id in "${plugin_ids[@]}"; do allow+=(--allow "$id"); done
    "$dotnet_bin" "$build_dir/tools/NKeeper.PluginTool/bin/Release/net10.0/nkeeper-plugin.dll" keygen --install \
        --out "$keys" --name install "${allow[@]}" --comment "install key, $(date -u +%Y-%m-%d)" >/dev/null
    mkdir -p "$build_dir/out/plugins"
    for project in "${plugin_projects[@]}"; do
        project=$build_dir/${project#"$REPO"/}
        "$dotnet_bin" build "$project" -c Release -p:NKeeperSigningKey="$keys/install.private.pem" \
            -p:NKeeperPluginPackages="$build_dir/out/plugins/" >/dev/null
    done
    rm -f "$keys/install.private.pem"
    for package in "$build_dir"/out/plugins/*.nkp; do
        "$dotnet_bin" "$build_dir/tools/NKeeper.PluginTool/bin/Release/net10.0/nkeeper-plugin.dll" verify "$package" \
            --trust "$keys" >/dev/null || die "$(basename "$package") didn't verify against the install key."
    done
    mv "$keys/install.trust.json" "$build_dir/out/install.json"
    rm -rf "$keys"
    ok "Built ${#plugin_ids[@]} built-in plugin(s): ${plugin_ids[*]}."
}

# --- Files ---------------------------------------------------------------------------------------

# replace_dir SOURCE TARGET: puts a copy of SOURCE at TARGET, root-owned and readable by everyone.
replace_dir() {
    rm -rf "$2.new"
    # Not --preserve=context: labels from the build cache would stop systemd starting the server.
    cp -R --preserve=mode,timestamps "$1" "$2.new"
    chown -R root:root "$2.new"
    chmod -R u=rwX,go=rX "$2.new"
    rm -rf "$2"
    mv "$2.new" "$2"
}

install_files() {
    step "Installing the files"
    id nkeeper >/dev/null 2>&1 || useradd --system --home-dir "$STATE" --shell /usr/sbin/nologin nkeeper
    install -d -o root -g root -m 0755 "$PREFIX" "$CONFIG" "$TRUST"
    install -d -o root -g root -m 0700 "$TLS"
    install -d -o nkeeper -g nkeeper -m 0750 "$STATE"

    systemctl stop nkeeper >/dev/null 2>&1 || true
    replace_dir "$build_dir/out/server" "$PREFIX/server"
    replace_dir "$build_dir/core/frontend/app/dist" "$PREFIX/shell"
    replace_dir "$build_dir/out/plugins" "$PREFIX/plugins"
    find "$PREFIX/plugins" -type f -exec chmod 0644 {} +
    # The previous install key goes, so packages it signed stop verifying.
    install -o root -g root -m 0644 "$build_dir/out/install.json" "$TRUST/install.json"

    if selinux; then
        semanage fcontext -a -t bin_t "$PREFIX/server/NKeeper\.Server" 2>/dev/null ||
            semanage fcontext -m -t bin_t "$PREFIX/server/NKeeper\.Server"
        restorecon -R "$PREFIX" >/dev/null
    fi
    ok "In $PREFIX."
}

# --- Settings ------------------------------------------------------------------------------------

fresh=true

configure() {
    step "Writing the settings"
    [ -n "$(get_env "$ENV_FILE" NKEEPER_INSTALLED)" ] && fresh=false
    touch "$ENV_FILE"
    chown root:root "$ENV_FILE"
    chmod 0600 "$ENV_FILE"

    set_env "$ENV_FILE" NKEEPER_DB_PASSWORD "$db_password"
    set_env "$ENV_FILE" ConnectionStrings__NKeeper "\"Host=127.0.0.1;Port=5432;Database=nkeeper;Username=nkeeper;Password=$db_password\""
    set_env "$ENV_FILE" ASPNETCORE_ENVIRONMENT Production
    set_env "$ENV_FILE" ASPNETCORE_URLS "http://127.0.0.1:$SERVER_PORT"
    set_env "$ENV_FILE" Frontend__Root "$PREFIX/shell"
    set_env "$ENV_FILE" ForwardedHeaders__Enabled true
    # The web domain: given now, or kept from the last install.
    web_domain=${web_domain:-$(get_env "$ENV_FILE" Origins__BaseDomain)}
    if [ -n "$web_domain" ]; then
        [[ $web_domain =~ ^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,62}$ ]] || die "--web-domain $web_domain isn't a DNS name such as web.example.com."
        set_env "$ENV_FILE" Origins__BaseDomain "$web_domain"
    fi
    set_env "$ENV_FILE" DataProtection__CertificatePath "$KEYRING"
    set_env "$ENV_FILE" DOTNET_CLI_TELEMETRY_OPTOUT 1

    # The key ring certificate (§6.3): a copy of the database alone can't decrypt the stored credentials.
    if [ ! -s "$KEYRING" ]; then
        (umask 077 && openssl req -x509 -newkey rsa:3072 -nodes -days 36500 -subj "/CN=NKeeper key ring" \
            -keyout "$KEYRING.key" -out "$KEYRING.crt" 2>/dev/null && cat "$KEYRING.crt" "$KEYRING.key" >"$KEYRING") ||
            die "Couldn't create $KEYRING."
        rm -f "$KEYRING.crt" "$KEYRING.key"
        warn "Created $KEYRING. Back it up apart from the database: without it, stored credentials can't be decrypted."
    fi
    chown root:nkeeper "$KEYRING"
    chmod 0640 "$KEYRING"

    if $fresh; then
        admin_email=${admin_email:-admin@$(hostname -f 2>/dev/null || hostname)}
        if [ -z "$admin_password" ] && ! $assume_yes && [ -t 0 ]; then
            read -rsp "Password for the first administrator, $admin_user (empty to generate one): " admin_password
            echo
        fi
        generated=false
        if [ -z "$admin_password" ]; then
            admin_password="Nk-$(random_text 8)-$(random_text 8)-A7"
            generated=true
        fi
        [ ${#admin_password} -ge 12 ] || die "The password needs at least 12 characters."
        set_env "$ENV_FILE" BootstrapAdmin__Enabled true
        set_env "$ENV_FILE" BootstrapAdmin__AllowOutsideDevelopment true
        set_env "$ENV_FILE" BootstrapAdmin__UserName "\"$admin_user\""
        set_env "$ENV_FILE" BootstrapAdmin__Email "\"$admin_email\""
        set_env "$ENV_FILE" BootstrapAdmin__Password "\"$admin_password\""
    else
        ok "Upgrading the existing install: data, settings and accounts are kept."
    fi

    install -o root -g root -m 0644 "$REPO/deploy/systemd/nkeeper.service" /etc/systemd/system/nkeeper.service
    systemctl daemon-reload
    ok "In $ENV_FILE."
}

# --- guacd -----------------------------------------------------------------------------------------

# The packages guacd builds against: FreeRDP (3 where there is one, else 2) and libvncserver, with their tools.
guacd_build_packages() {
    if [ "$pm" = apt ]; then
        local freerdp=freerdp3-dev
        apt-cache show freerdp3-dev >/dev/null 2>&1 || freerdp=freerdp2-dev
        apt-get install -y -qq build-essential pkg-config libtool-bin libcairo2-dev libjpeg-dev libpng-dev uuid-dev \
            libssl-dev libwebp-dev libvncserver-dev "$freerdp" >/dev/null
    else
        # FreeRDP's and libvncserver's development packages are in CRB and EPEL on the RHEL family.
        if [ -f /etc/os-release ] && ! grep -qi '^ID="\?fedora' /etc/os-release; then
            dnf install -y -q dnf-plugins-core >/dev/null
            dnf config-manager --set-enabled crb >/dev/null 2>&1 || dnf config-manager --set-enabled powertools >/dev/null 2>&1 || true
            rpm -q epel-release >/dev/null 2>&1 || dnf install -y -q epel-release >/dev/null
        fi
        dnf install -y -q gcc make pkgconf libtool cairo-devel libjpeg-turbo-devel libpng-devel libuuid-devel \
            openssl-devel libwebp-devel libvncserver-devel freerdp-devel >/dev/null
    fi
}

build_guacd() {
    step "Building guacd $GUACD_VERSION (a few minutes)"
    guacd_build_packages
    local src=$CACHE/guacd tarball="guacamole-server-$GUACD_VERSION.tar.gz" url
    rm -rf "$src" && mkdir -p "$src"
    for url in "https://downloads.apache.org/guacamole/$GUACD_VERSION/source/$tarball" \
               "https://archive.apache.org/dist/guacamole/$GUACD_VERSION/source/$tarball"; do
        curl -fsSL "$url" -o "$src/$tarball" && break
    done
    [ -s "$src/$tarball" ] || die "Couldn't download guacamole-server $GUACD_VERSION."
    echo "$GUACD_SHA256  $src/$tarball" | sha256sum -c --quiet - || die "The guacamole-server download failed its checksum."
    tar -xzf "$src/$tarball" -C "$src" --strip-components=1

    # RDP and VNC only: NKeeper has its own SSH, and guacd needs no terminal, Kubernetes or recording tools.
    (cd "$src" && ./configure --prefix="$GUACD_PREFIX" --with-freerdp-plugin-dir="$GUACD_PREFIX/lib/freerdp" \
        --without-ssh --without-telnet --without-websockets --without-pango --without-terminal \
        --disable-guacenc --disable-guaclog --disable-kubernetes >"$src/configure.log" 2>&1) ||
        { tail -n 30 "$src/configure.log" >&2; die "guacamole-server's configure failed (above)."; }
    grep -q 'RDP .*\.\.\. yes' "$src/configure.log" || grep -q 'RDP *\.\.\. yes' "$src/configure.log" ||
        { grep -A20 'Protocol support' "$src/configure.log" >&2; die "guacamole-server would build without RDP support (FreeRDP not found)."; }
    (cd "$src" && make -j"$(nproc)" >"$src/build.log" 2>&1 && rm -rf "$GUACD_PREFIX" && make install >>"$src/build.log" 2>&1) ||
        { tail -n 30 "$src/build.log" >&2; die "Building guacd failed (above)."; }
    chown -R root:root "$GUACD_PREFIX"
    chmod -R u=rwX,go=rX "$GUACD_PREFIX"
    echo "$GUACD_VERSION" >"$GUACD_PREFIX/.version"
    ok "Built guacd $GUACD_VERSION into $GUACD_PREFIX."
}

install_guacd() {
    if $no_guacd; then
        warn "Skipping guacd (--no-guacd): remote desktops won't work."
        return
    fi
    if [ "$(cat "$GUACD_PREFIX/.version" 2>/dev/null)" != "$GUACD_VERSION" ] || [ ! -x "$GUACD_PREFIX/sbin/guacd" ]; then
        build_guacd
    else
        ok "guacd $GUACD_VERSION is already built."
    fi

    id nkeeper-guacd >/dev/null 2>&1 || useradd --system --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin nkeeper-guacd
    if selinux; then
        semanage fcontext -a -t bin_t "$GUACD_PREFIX/sbin/guacd" 2>/dev/null ||
            semanage fcontext -m -t bin_t "$GUACD_PREFIX/sbin/guacd"
        restorecon -R "$GUACD_PREFIX" >/dev/null
    fi
    install -o root -g root -m 0644 "$REPO/deploy/systemd/nkeeper-guacd.service" /etc/systemd/system/nkeeper-guacd.service
    systemctl daemon-reload
    systemctl enable nkeeper-guacd >/dev/null 2>&1
    systemctl restart nkeeper-guacd
    local i
    for ((i = 0; i < 30; i++)); do
        ss -ltn 2>/dev/null | grep -q '127.0.0.1:4822 ' && break
        sleep 1
    done
    if ss -ltn 2>/dev/null | grep -q '127.0.0.1:4822 '; then
        ok "guacd listens on 127.0.0.1:4822."
    else
        journalctl -u nkeeper-guacd -n 20 --no-pager >&2 || true
        warn "guacd didn't start (log above): remote desktops won't work until it does."
    fi
}

# --- nginx ---------------------------------------------------------------------------------------

default_names() {
    hostname -f 2>/dev/null || true
    hostname -s 2>/dev/null || true
    hostname -I 2>/dev/null | tr ' ' '\n' | grep -v '^$' || true
}

# Whether nginx's certificate is one this installer made: marked as such, or (from before the mark) self-signed.
installer_certificate() {
    [ -f "$TLS/self-signed-for" ] && return 0
    [ -f "$TLS/custom" ] && return 1
    [ "$(openssl x509 -in "$TLS/nkeeper.crt" -noout -issuer 2>/dev/null | sed 's/^issuer= *//')" =       "$(openssl x509 -in "$TLS/nkeeper.crt" -noout -subject 2>/dev/null | sed 's/^subject= *//')" ]
}

setup_nginx() {
    step "Configuring nginx with HTTPS"
    if [ ${#names[@]} -eq 0 ]; then
        mapfile -t names < <(default_names | awk 'NF && !seen[$0]++')
    fi
    [ ${#names[@]} -gt 0 ] || die "Couldn't tell this machine's names; give one with --server-name."

    # Web pages' session origins (docs/architecture.md §7.18): nkw-<id>.<web domain>, answered by the same server.
    local all_names=("${names[@]}")
    if [ -n "$web_domain" ]; then
        all_names+=("*.$web_domain")
    fi

    if [ -n "$cert" ]; then
        [ -n "$key" ] || die "--cert needs --key."
        install -o root -g root -m 0644 "$cert" "$TLS/nkeeper.crt"
        install -o root -g root -m 0600 "$key" "$TLS/nkeeper.key"
        rm -f "$TLS/self-signed-for"
        touch "$TLS/custom"
    elif [ ! -s "$TLS/nkeeper.crt" ] || { installer_certificate && [ "$(cat "$TLS/self-signed-for" 2>/dev/null)" != "${all_names[*]}" ]; }; then
        # A certificate this installer made is made again when the names change; one given with --cert never is.
        local san="" name
        for name in "${all_names[@]}"; do
            if [[ $name =~ ^[0-9.]+$ || $name == *:* ]]; then san+="IP:$name,"; else san+="DNS:$name,"; fi
        done
        (umask 077 && openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 825 \
            -subj "/CN=${names[0]}" -addext "subjectAltName=${san%,}" \
            -keyout "$TLS/nkeeper.key" -out "$TLS/nkeeper.crt" 2>/dev/null) || die "Couldn't create a certificate."
        chmod 0644 "$TLS/nkeeper.crt"
        echo "${all_names[*]}" >"$TLS/self-signed-for"
        rm -f "$TLS/custom"
        warn "nginx uses a self-signed certificate for ${all_names[*]}; give your own with --cert and --key."
    fi

    # IPv6 addresses stay in the certificate, but not in server_name: the HTTPS server is the default one anyway.
    local server_names
    server_names=$(printf '%s\n' "${all_names[@]}" | grep -v ':' | tr '\n' ' ')
    # nginx 1.25.1 moved HTTP/2 from `listen … http2` to its own directive, and warns about the old form.
    local listen_http2=" http2" http2="" version
    version=$(nginx -v 2>&1 | sed -n 's|.*nginx/\([0-9.]*\).*|\1|p')
    if [ -n "$version" ] && [ "$(printf '%s\n' 1.25.1 "$version" | sort -V | head -n 1)" = 1.25.1 ]; then
        listen_http2="" http2="http2 on;"
    fi
    sed -e "s|@SERVER_NAMES@|${server_names% }|g" -e "s|@TLS_DIR@|$TLS|g" \
        -e "s|@LISTEN_HTTP2@|$listen_http2|g" -e "s|@HTTP2@|$http2|g" \
        "$REPO/deploy/nginx/nkeeper.conf" >/etc/nginx/conf.d/nkeeper.conf
    chmod 0644 /etc/nginx/conf.d/nkeeper.conf
    # Debian's default site would otherwise answer on port 80 first.
    rm -f /etc/nginx/sites-enabled/default

    if selinux; then
        setsebool -P httpd_can_network_connect 1
        restorecon -R "$TLS" >/dev/null
    fi
    nginx -t -q || die "nginx refused the configuration (see above)."
    systemctl enable nginx >/dev/null 2>&1
    systemctl reload-or-restart nginx
    ok "Serving ${all_names[*]}."
}

open_firewall() {
    if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
        firewall-cmd -q --permanent --add-service=https --add-service=http
        firewall-cmd -q --reload
        ok "Opened HTTPS and HTTP in firewalld."
    elif command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q 'Status: active'; then
        ufw allow 443/tcp >/dev/null
        ufw allow 80/tcp >/dev/null
        ok "Opened ports 443 and 80 in ufw."
    fi
}

# --- Start ---------------------------------------------------------------------------------------

wait_for_server() {
    local i
    for ((i = 0; i < 120; i++)); do
        curl -fs "http://127.0.0.1:$SERVER_PORT/health/live" >/dev/null 2>&1 && return 0
        sleep 1
    done
    return 1
}

start() {
    step "Starting NKeeper"
    systemctl enable nkeeper >/dev/null 2>&1
    systemctl restart nkeeper
    if ! wait_for_server; then
        journalctl -u nkeeper -n 40 --no-pager >&2 || true
        die "NKeeper didn't start in time (log above). See: journalctl -u nkeeper"
    fi
    set_env "$ENV_FILE" NKEEPER_INSTALLED 1
    if $fresh; then
        # The administrator exists now: the bootstrap goes, and so does its password.
        set_env "$ENV_FILE" BootstrapAdmin__Enabled false
        set_env "$ENV_FILE" BootstrapAdmin__Password '""'
        systemctl restart nkeeper
        wait_for_server || die "NKeeper didn't restart (journalctl -u nkeeper)."
    fi
    ok "NKeeper is up."
}

summary() {
    echo
    echo "${bold}NKeeper is installed.${reset} Open https://${names[0]}/"
    if $fresh; then
        echo "  Sign in as ${bold}$admin_user${reset}."
        if ${generated:-false}; then
            echo "  Password: ${bold}$admin_password${reset}  (shown only now; it isn't stored anywhere)"
        fi
    fi
    echo "  Logs:     journalctl -u nkeeper"
    echo "  Back up:  the nkeeper database, and $KEYRING apart from it."
}

# --- Commands ------------------------------------------------------------------------------------

do_install() {
    [ "$(id -u)" -eq 0 ] || die "The installer sets up system services: run it with sudo."
    install_packages
    setup_postgres
    fetch_toolchain
    build
    install_files
    install_guacd
    configure
    setup_nginx
    open_firewall
    start
    summary
}

do_uninstall() {
    [ "$(id -u)" -eq 0 ] || die "Run it with sudo."
    step "Removing NKeeper"
    systemctl disable --now nkeeper nkeeper-guacd >/dev/null 2>&1 || true
    rm -f /etc/systemd/system/nkeeper.service /etc/systemd/system/nkeeper-guacd.service
    systemctl daemon-reload
    rm -rf "$PREFIX" "$CACHE" /etc/nginx/conf.d/nkeeper.conf
    systemctl reload nginx >/dev/null 2>&1 || true
    if $purge; then
        postgres_psql -qc 'DROP DATABASE IF EXISTS nkeeper' -c 'DROP ROLE IF EXISTS nkeeper' >/dev/null 2>&1 || true
        rm -rf "$CONFIG" "$STATE"
        userdel nkeeper >/dev/null 2>&1 || true
        userdel nkeeper-guacd >/dev/null 2>&1 || true
        ok "Removed, with the database and $CONFIG."
    else
        ok "Removed. The nkeeper database and $CONFIG are kept; --purge removes them."
    fi
}

case $command in
    install) do_install ;;
    uninstall) do_uninstall ;;
    ""|-h|--help|help) usage ;;
    *) die "Unknown command: $command (see --help)" ;;
esac
