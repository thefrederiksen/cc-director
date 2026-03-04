namespace CcDirector.Engine.Dispatcher;

/// <summary>
/// A discovered route from an email address to the tool that can send from it.
/// </summary>
public sealed record EmailRoute(string EmailAddress, string ToolPath, string ToolName, string AccountName);
