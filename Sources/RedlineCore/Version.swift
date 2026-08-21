// The version, for builds that have no Info.plist to read it from.
// VersionTests pins this to Resources/Info.plist and project.yml so a release cannot bump one
// and forget the others.
import Foundation

public enum RedlineVersion {
    public static let current = "0.8.3"
}
