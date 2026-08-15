# Homebrew cask for RedLine.
#
# Install straight from this repo without a tap:
#   brew install --cask ./Casks/redline.rb
#
# Or publish a tap (repo named homebrew-tap) and install with:
#   brew tap goriparthi/tap && brew install --cask redline
#
# Bump version and sha256 after each release; scripts/release.sh prints both.
cask "redline" do
  version "0.1.6"
  sha256 "deccc0be53dcffca90feeb1f602736ba3e4ab8c82702dc7c7f80558ebe8e606f"

  url "https://github.com/goriparthi/redline/releases/download/v#{version}/Redline-#{version}.dmg"
  name "RedLine"
  desc "Menu bar monitor for AI coding agent usage and rate limits"
  homepage "https://github.com/goriparthi/redline"

  depends_on macos: ">= :sonoma"

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
