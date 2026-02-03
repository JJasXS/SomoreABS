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
            { "1", "#FFB6C1" }, // Light Pink
            { "2", "#ADD8E6" }, // Light Blue
            { "3", "#90EE90" }, // Light Green
            { "4", "#FFD700" }  // Gold
        };
    }
}
