namespace YouTube
{
    // Keeps the unfinished or submitted query while pages and their Navbar instances change.
    // This is intentionally session-only: navigation should preserve the text without turning
    // it into a permanent preference that reappears after the application is restarted.
    internal static class SearchQueryStateController
    {
        private static string _currentQuery = string.Empty;

        internal static string CurrentQuery
        {
            get { return _currentQuery; }
        }

        internal static void SetCurrentQuery(string query)
        {
            _currentQuery = query ?? string.Empty;
        }
    }
}
