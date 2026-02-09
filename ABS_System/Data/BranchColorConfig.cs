// Branch color mapping for calendar UI
// Usage: Reference this dictionary to get color for a given BRANCHNO (1-4)

using System.Collections.Generic;

namespace YourApp.Data
{
    public static class BranchColorConfig
    {
        // Map branch numbers to color hex codes
        public static readonly Dictionary<string, string> BranchColors = new()
        {
            { "1", "#ffb6c1" }, // Light Pink
            { "2", "#9ae6ff" }, // Light Blue
            { "3", "#74db74" }, // Light Green
            { "4", "#e2ce5a" }  // Gold
        };
    }
}
