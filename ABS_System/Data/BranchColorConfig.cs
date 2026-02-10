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
            { "1", "#00ff2a" }, // Light Pink
            { "2", "#ff0000" }, // Light Blue
            { "3", "#2600ff" }, // Light Green
            { "4", "#ff00b3" }  // Gold
        };
    }
}
