namespace Contain.Viewer;

/// <summary>Expose exact record identity to accessibility clients without another visible column.</summary>
internal sealed class IdentityTextBoxCell : DataGridViewTextBoxCell
{
    internal string? Identity { get; set; }

    public override object Clone()
    {
        var copy = (IdentityTextBoxCell)base.Clone();
        copy.Identity = Identity;
        return copy;
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new IdentityCellAccessibleObject(this);

    private sealed class IdentityCellAccessibleObject : DataGridViewTextBoxCellAccessibleObject
    {
        private readonly IdentityTextBoxCell cell;
        internal IdentityCellAccessibleObject(IdentityTextBoxCell cell) : base(cell) => this.cell = cell;

        // The standard cell recomputes Name, so assigning its setter does not
        // survive a UIA query. Retain its native Value/GridItem/TableItem/Invoke
        // behavior and override only the identity-bearing accessible name.
        public override string? Name
        {
            get => string.IsNullOrEmpty(cell.Identity) ? base.Name : base.Name + "；" + cell.Identity;
            set => base.Name = value;
        }
    }
}
