# Beta channel cask for RedLine. Tracks prerelease versions (x.y.z-beta.n) published as
# GitHub prereleases; the stable channel lives in redline.rb.
#
# Install straight from this repo without a tap:
#   brew install --cask ./Casks/redline-beta.rb
#
# Bump version and sha256 after each beta release; scripts/release.sh prints both.
cask "redline-beta" do
  version "0.8.3-beta.1"
  sha256 "05bc15b592e5f5dd10aee9468b64ad2c9f362ba102f96b8b534a7b8323066469"

  url "https://github.com/goriparthi/redline/releases/download/v#{version}/Redline-#{version}.dmg"
  name "RedLine Beta"
  desc "Menu bar monitor for AI coding agent usage and rate limits (beta channel)"
  homepage "https://github.com/goriparthi/redline"

  depends_on macos: ">= :sonoma"
  conflicts_with cask: "redline"

  app "Redline.app"

  # The app runs as a LaunchAgent so the menu bar item survives logout and restart
  postflight do
    system_command "#{staged_path}/Redline.app/Contents/MacOS/redline",
                   args: ["--install-launch-agent"],
                   must_succeed: false
  end

  uninstall launchctl: "com.goriparthi.redline",
            quit:      "com.goriparthi.redline"

  zap trash: [
    "~/.config/redline",
    # Snapshot, recorded history, alert state and the feed script all live here
    "~/.local/share/redline",
    "~/Library/Logs/redline.log",
    "~/Library/Logs/redline.err",
    "~/Library/LaunchAgents/com.goriparthi.redline.plist",
  ]
end
