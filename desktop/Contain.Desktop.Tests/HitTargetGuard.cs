namespace Contain.Desktop.Tests;

/// <summary>The actual native click guard. A same-process window alone does not prove the intended control is hit.</summary>
internal static class HitTargetGuard
{
    internal const int MaximumNodes = 16;
    internal sealed record Result(bool Matches, string Reason);
    internal readonly record struct Rectangle(double X, double Y, double Width, double Height)
    {
        internal bool Valid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width)
            && double.IsFinite(Height) && double.IsFinite(X + Width) && double.IsFinite(Y + Height)
            && Width > 0 && Height > 0;
        internal bool Contains(double x, double y) => Valid && double.IsFinite(x) && double.IsFinite(y)
            && x >= X && y >= Y && x <= X + Width && y <= Y + Height;
        internal bool Contains(Rectangle other) => other.Valid && Contains(other.X, other.Y)
            && Contains(other.X + other.Width, other.Y + other.Height);
    }
    internal sealed record Viewport(string Role, bool Owned, bool IsViewport, bool ContainsTarget,
        bool ContainsPoint, bool IsExactMainWindow);

    internal static Result CheckGeometry<T>(T? node, Rectangle target, Rectangle window, double x, double y,
        Func<T, Viewport> inspect, Func<T, T?> parent, Action<Viewport> observe) where T : class
    {
        if (!target.Contains(x, y) || !window.Contains(target)) return new(false, "outside_main_window");
        for (var index = 0; index < MaximumNodes; index++)
        {
            if (node is null) return new(false, "main_window_not_reached");
            var viewport = inspect(node); observe(viewport);
            if (!viewport.Owned) return new(false, "foreign_ancestor");
            if (viewport.IsViewport && (!viewport.ContainsTarget || !viewport.ContainsPoint))
                return new(false, "clipped_by_scroll_viewport");
            if (viewport.IsExactMainWindow)
                return new(viewport.ContainsTarget && viewport.ContainsPoint, "owned_main_window_reached");
            if (index + 1 < MaximumNodes) node = parent(node);
        }
        return new(false, "ancestor_limit");
    }
    internal sealed record Identity(string Role, bool Owned, bool RequiredIdMatches, bool RoleMatches,
        bool BoundsMatch, bool Enabled, bool Visible, bool ContainsClickPoint, bool RuntimeMatches)
    {
        // Runtime IDs remain diagnostic: the user-facing action contract is its unique,
        // owned, fixed AutomationId and current button geometry in the actual hit ancestry.
        internal bool Matches => Owned && RequiredIdMatches && RoleMatches && BoundsMatch
            && Enabled && Visible && ContainsClickPoint;
    }

    internal static void RequireUniqueCount(int count)
    {
        if (count != 1) throw new ClickTargetIdentityException(count == 0 ? "missing_fixed_id" : "duplicate_fixed_id");
    }

    internal static Result Check<T>(T target, T? hit, Func<T, bool> owned, Func<T, T, bool> same,
        Func<T, T?> parent, Action<T> observe) where T : class
    {
        var current = hit;
        for (var index = 0; index < MaximumNodes; index++)
        {
            if (current is null) return new(false, "target_not_hit");
            observe(current);
            if (!owned(current)) return new(false, "uia_foreign_owner");
            if (same(target, current)) return new(true, "exact_target_or_descendant");
            if (index + 1 < MaximumNodes) current = parent(current);
        }
        return new(false, "ancestor_limit");
    }

    internal static void VerifyContract()
    {
        RequireUniqueCount(1);
        foreach (var count in new[] { 0, 2 })
        {
            try { RequireUniqueCount(count); throw new InvalidOperationException("Non-unique click target was accepted."); }
            catch (ClickTargetIdentityException) { }
        }
        var identity = new Identity("ControlType.Button", true, true, true, true, true, true, true, false);
        if (!identity.Matches) throw new InvalidOperationException("Exact action identity must not require equal runtime IDs.");
        foreach (var invalid in new[]
        {
            identity with { Owned = false }, identity with { RequiredIdMatches = false, RuntimeMatches = true },
            identity with { RoleMatches = false }, identity with { BoundsMatch = false },
            identity with { Enabled = false }, identity with { Visible = false }, identity with { ContainsClickPoint = false }
        })
            if (invalid.Matches) throw new InvalidOperationException("Incomplete action identity was accepted.");
        var target = new Node();
        var child = new Node { Parent = target };
        var sibling = new Node();
        var foreign = new Node { Parent = target, Owned = false };
        var observed = 0;
        Result Probe(Node? hit) => Check(target, hit, n => n.Owned, ReferenceEquals,
            n => n.Parent, _ => observed++);
        if (!Probe(target).Matches || !Probe(child).Matches)
            throw new InvalidOperationException("Exact click target and its content must be accepted.");
        if (Probe(sibling).Matches || Probe(null).Matches || Probe(foreign).Matches)
            throw new InvalidOperationException("A covered, absent, or foreign click target must be rejected.");
        var tooDeep = target;
        for (var index = 0; index < MaximumNodes; index++) tooDeep = new Node { Parent = tooDeep };
        observed = 0;
        if (Probe(tooDeep).Reason != "ancestor_limit" || observed != MaximumNodes)
            throw new InvalidOperationException("Click ancestry must retain its fixed traversal bound.");
        var original = new System.Runtime.InteropServices.COMException("SYNTHETIC hit-test failure");
        try
        {
            Check(target, child, _ => throw original, ReferenceEquals, n => n.Parent, _ => { });
            throw new InvalidOperationException("Hit-test provider exception was swallowed.");
        }
        catch (System.Runtime.InteropServices.COMException error) when (ReferenceEquals(original, error)) { }

        var fullWindow = new Rectangle(0, 0, 1024, 720);
        var action = new Rectangle(400, 350, 120, 32);
        var main = new GeometryNode(fullWindow, false, true);
        var scroll = new GeometryNode(new(200, 300, 800, 350), true, false, main);
        Result Geometry(Rectangle bounds, GeometryNode? node) => CheckGeometry(node, bounds, fullWindow,
            bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2,
            n => new("ControlType.Pane", n.Owned, n.IsViewport, n.Bounds.Contains(bounds),
                n.Bounds.Contains(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), n.IsMain), n => n.Parent, _ => { });
        if (!Geometry(action, scroll).Matches) throw new InvalidOperationException("Visible action inside its owned viewport must pass.");
        foreach (var invalid in new[]
        {
            Geometry(new(400, 250, 120, 32), scroll), // M5: toolbar inside HWND but above event viewport.
            Geometry(action, new(new(0, 0, 100, 100), true, false, main)),
            Geometry(new(-1, 350, 120, 32), main), Geometry(new(double.NaN, 350, 120, 32), main),
            Geometry(action, new(fullWindow, false, true, null, false)),
            Geometry(action, new(fullWindow, false, false)), Geometry(action, null)
        })
            if (invalid.Matches) throw new InvalidOperationException("Clipped, foreign, invalid or unrooted action geometry was accepted.");
        var deepGeometry = main;
        for (var index = 0; index < MaximumNodes; index++) deepGeometry = new(fullWindow, false, false, deepGeometry);
        if (Geometry(action, deepGeometry).Reason != "ancestor_limit")
            throw new InvalidOperationException("Geometry ancestry must retain its fixed bound.");
        try
        {
            CheckGeometry(main, action, fullWindow, 450, 360, _ => throw original, n => n.Parent, _ => { });
            throw new InvalidOperationException("Geometry provider exception was swallowed.");
        }
        catch (System.Runtime.InteropServices.COMException error) when (ReferenceEquals(original, error)) { }
    }

    private sealed record GeometryNode(Rectangle Bounds, bool IsViewport, bool IsMain,
        GeometryNode? Parent = null, bool Owned = true);

    private sealed class Node
    {
        internal Node? Parent { get; init; }
        internal bool Owned { get; init; } = true;
    }

    internal sealed class ClickTargetIdentityException(string reason) : InvalidOperationException("Click target identity rejected: " + reason);
}
