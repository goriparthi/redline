# Beta channel cask for RedLine. Tracks prerelease versions (x.y.z-beta.n) published as
# GitHub prereleases; the stable channel lives in redline.rb.
#
# Install straight from this repo without a tap:
#   brew install --cask ./Casks/redline-beta.rb
#
# Bump version and sha256 after each beta release; scripts/release.sh prints both.
cask "redline-beta" do
  version "0.4.0-beta.1"
  sha256 "407e10a873b0ae3825a46e097d0d1805b50821937cf3bd235adb215101ed3b69"

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
    "~/Library/Logs/redline.log",
    "~/Library/Logs/redline.err",
    "~/Library/LaunchAgents/com.goriparthi.redline.plist",
  ]
end
