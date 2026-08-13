# Every target delegates to a script in scripts/, so the Makefile stays a thin index and
# CI, humans and agents all run the identical code path.
.PHONY: help build test bundle install uninstall purge dmg release clean widget xcodeproj

help:
	@echo "redline targets:"
	@echo "  make build      Release build of the binary"
	@echo "  make test       Run the test suite (auto-selects an Xcode with XCTest)"
	@echo "  make bundle     Assemble dist/Redline.app and sign it"
	@echo "  make widget     Build app + desktop widget via Xcode (needs Xcode)"
	@echo "  make xcodeproj  Regenerate Redline.xcodeproj from project.yml"
	@echo "  make install    Build, install to ~/Applications, load the LaunchAgent"
	@echo "  make uninstall  Remove app, LaunchAgent and Keychain token (keeps config)"
	@echo "  make purge      Same as uninstall, and delete config and logs"
	@echo "  make dmg        Build dist/Redline-<version>.dmg (notarizes when configured)"
	@echo "  make release    Test, build DMG, publish a GitHub release"
	@echo "  make clean      Remove .build and dist"

build:
	@scripts/build.sh

test:
	@scripts/test.sh

bundle:
	@scripts/bundle.sh

widget:
	@scripts/build-widget.sh

xcodeproj:
	@xcodegen generate

install:
	@scripts/install.sh

uninstall:
	@scripts/uninstall.sh

purge:
	@scripts/uninstall.sh --purge

dmg:
	@scripts/package-dmg.sh

release:
	@scripts/release.sh

clean:
	@rm -rf .build dist
	@echo "Cleaned .build and dist"
