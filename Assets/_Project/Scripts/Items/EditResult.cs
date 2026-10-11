namespace Blackglass
{
    /// <summary>The outcome of an equipment edit: Ok, or the reason text to show.</summary>
    public readonly struct EditResult
    {
        EditResult(bool ok, string reason)
        {
            Ok = ok;
            Reason = reason ?? string.Empty;
        }

        public bool Ok { get; }
        public string Reason { get; }

        public static EditResult Success => new EditResult(true, string.Empty);

        public static EditResult Fail(string reason) => new EditResult(false, reason);
    }
}
