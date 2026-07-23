namespace Logrr.Client
{
    /// <summary>Severity, matching the Logrr server's level scale.</summary>
    public enum LogrrLevel
    {
        Verbose = 0,
        Debug = 1,
        Information = 2,
        Warning = 3,
        Error = 4,
        Fatal = 5,
    }

    internal static class LevelNames
    {
        public static string ToName(LogrrLevel level)
        {
            switch (level)
            {
                case LogrrLevel.Verbose: return "Verbose";
                case LogrrLevel.Debug: return "Debug";
                case LogrrLevel.Warning: return "Warning";
                case LogrrLevel.Error: return "Error";
                case LogrrLevel.Fatal: return "Fatal";
                default: return "Information";
            }
        }
    }
}
