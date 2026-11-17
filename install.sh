#!/usr/bin/env bash
# Installs or updates the Vertex command line client (vx) on Linux or macOS.
#
#   curl -fsSL https://raw.githubusercontent.com/EntexInteractive/Vertex/main/install.sh | bash
#
# Options (after "bash -s --" when piping):
#   --version <v>    install a specific release, e.g. 1.3.0 or 1.3.0-beta.1 (default: the newest release,
#                    including pre-releases)
#   --prefix <dir>   install under <dir>/lib/vertex with a link in <dir>/bin (default: /usr/local, or ~/.local when
#                    /usr/local isn't writable and sudo isn't available)
#   --uninstall      remove vx
#
# Re-running it updates vx in place. Logins and settings in ~/.vertex are never touched.

set -euo pipefail

REPO="EntexInteractive/Vertex"
APP="vx"

version=""
prefix=""
uninstall=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) version="${2:?--version needs a value}"; shift 2 ;;
        --prefix) prefix="${2:?--prefix needs a value}"; shift 2 ;;
        --uninstall) uninstall=true; shift ;;
        -h|--help) sed -n '2,14p' "$0" 2>/dev/null | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown option '$1'. Run with --help for usage." >&2; exit 1 ;;
    esac
done

fail() { echo "error: $*" >&2; exit 1; }

command -v curl >/dev/null || fail "curl is required. Install it with your package manager (e.g. 'sudo apt install curl')."
command -v tar >/dev/null || fail "tar is required."

# --- Where to install --------------------------------------------------------------------------------------------
# Use sudo only for the system prefix, and only when it's needed.
sudo=""
if [[ -z "$prefix" ]]; then
    prefix="/usr/local"
    if [[ ! -w "$prefix" && $EUID -ne 0 ]]; then
        if command -v sudo >/dev/null; then
            sudo="sudo"
        else
            prefix="$HOME/.local"
        fi
    fi
elif [[ ! -w "$prefix" && -e "$prefix" && $EUID -ne 0 ]] && command -v sudo >/dev/null; then
    sudo="sudo"
fi

install_dir="$prefix/lib/vertex"
bin_link="$prefix/bin/$APP"

if $uninstall; then
    $sudo rm -rf "$install_dir"
    [[ -L "$bin_link" ]] && $sudo rm -f "$bin_link"
    echo "Removed $APP from $prefix. Your logins and settings in ~/.vertex were kept; delete that folder to remove them too."
    exit 0
fi

# --- Which build ---------------------------------------------------------------------------------------------------
case "$(uname -s)" in
    Linux) os="linux" ;;
    Darwin) os="osx" ;;
    *) fail "Unsupported system '$(uname -s)'. On Windows, download vx-win-x64.zip from https://github.com/$REPO/releases." ;;
esac

case "$(uname -m)" in
    x86_64|amd64) arch="x64" ;;
    aarch64|arm64) arch="arm64" ;;
    *) fail "Unsupported processor '$(uname -m)'. Builds exist for x64 and arm64." ;;
esac

asset="$APP-$os-$arch.tar.gz"

# --- Which release -------------------------------------------------------------------------------------------------
# /releases/latest skips pre-releases, so ask for the newest release of any kind. Parsed with sed so jq isn't needed.
if [[ -z "$version" ]]; then
    echo "Finding the newest release..."
    tag=$(curl -fsSL "https://api.github.com/repos/$REPO/releases?per_page=1" \
        | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -n 1) || true
    [[ -n "$tag" ]] || fail "Couldn't find a release of $REPO. Check your connection, or pass --version."
else
    tag="v${version#v}"
fi

url="https://github.com/$REPO/releases/download/$tag/$asset"

# --- Download and install ------------------------------------------------------------------------------------------
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Downloading $APP $tag ($os-$arch)..."
curl -fSL --progress-bar -o "$tmp/$asset" "$url" || fail "Couldn't download $url. Check that release $tag exists and has a $asset file."

mkdir -p "$tmp/extract"
tar -xzf "$tmp/$asset" -C "$tmp/extract"
[[ -f "$tmp/extract/$APP" ]] || fail "The download doesn't contain '$APP'. Please report this at https://github.com/$REPO/issues."
chmod 755 "$tmp/extract/$APP"

# Replace the old install only once the new one is fully downloaded and extracted.
[[ -n "$sudo" ]] && echo "Installing to $prefix (needs sudo)..." || echo "Installing to $prefix..."
$sudo mkdir -p "$prefix/lib" "$prefix/bin"
$sudo rm -rf "$install_dir.new"
$sudo cp -R "$tmp/extract" "$install_dir.new"
$sudo rm -rf "$install_dir"
$sudo mv "$install_dir.new" "$install_dir"
$sudo ln -sfn "$install_dir/$APP" "$bin_link"

# --- Checks --------------------------------------------------------------------------------------------------------
# .NET needs ICU for culture data on Linux; without it vx exits at startup with a globalization error.
if [[ "$os" == "linux" ]] && command -v ldconfig >/dev/null && ! ldconfig -p 2>/dev/null | grep -q libicuuc; then
    echo
    echo "warning: ICU wasn't found, and vx needs it. Install it first:"
    echo "  Debian/Ubuntu: sudo apt install libicu-dev    Fedora/RHEL: sudo dnf install libicu    Alpine: sudo apk add icu-libs"
fi

case ":$PATH:" in
    *":$prefix/bin:"*) ;;
    *)
        echo
        echo "Add $prefix/bin to your PATH to run '$APP' from anywhere:"
        echo "  echo 'export PATH=\"$prefix/bin:\$PATH\"' >> ~/.bashrc && source ~/.bashrc"
        ;;
esac

echo
echo "Installed $APP $tag to $bin_link."
echo "Next: vx config server https://your-vertex-server && vx login"
