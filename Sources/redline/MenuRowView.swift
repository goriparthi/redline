// A menu row that is information rather than a control.
//
// An NSMenuItem with no action still highlights on hover once the menu stops auto-enabling
// items, which made status text look clickable. An item that carries a view draws only what
// the view draws, so this keeps full contrast and stays visibly inert.
import AppKit

final class MenuRowView: NSView {
    // Matches where AppKit starts menu item text, so these rows line up with real controls
    private static let leadingInset: CGFloat = 22
    private static let trailingInset: CGFloat = 16
    private static let verticalPadding: CGFloat = 3

    init(attributed: NSAttributedString) {
        super.init(frame: .zero)
        let label = NSTextField(labelWithAttributedString: attributed)
        label.translatesAutoresizingMaskIntoConstraints = false
        label.lineBreakMode = .byClipping
        label.isSelectable = false
        addSubview(label)
        NSLayoutConstraint.activate([
            label.leadingAnchor.constraint(equalTo: leadingAnchor,
                                           constant: Self.leadingInset),
            label.centerYAnchor.constraint(equalTo: centerYAnchor),
        ])
        let size = label.intrinsicContentSize
        frame = NSRect(x: 0, y: 0,
                       width: size.width + Self.leadingInset + Self.trailingInset,
                       height: size.height + Self.verticalPadding * 2)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("not used") }

    // Nothing here responds to the mouse, so the row cannot be mistaken for a control
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}
