namespace LibShared;
public static class Info { public static string Version => typeof(Info).Assembly.GetName().Version!.ToString(); }
