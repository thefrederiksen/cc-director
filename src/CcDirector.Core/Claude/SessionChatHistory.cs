using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Thread-safe, per-session store of Simple Chat messages.
/// </summary>
public sealed class SessionChatHistory
{
    private readonly List<ChatMessage> _messages = new();
    private readonly object _lock = new();

    /// <summary>Fires on the calling thread when a message is added.</summary>
    public event Action<ChatMessage>? MessageAdded;

    /// <summary>Add a message and notify subscribers.</summary>
    public void AddMessage(ChatMessage message)
    {
        FileLog.Write($"[SessionChatHistory] AddMessage: type={message.Type}, textLen={message.Text.Length}");
        lock (_lock)
        {
            _messages.Add(message);
        }
        MessageAdded?.Invoke(message);
    }

    /// <summary>Return a snapshot of all messages.</summary>
    public List<ChatMessage> GetMessages()
    {
        lock (_lock)
        {
            return _messages.ToList();
        }
    }

    /// <summary>Number of messages in the history.</summary>
    public int Count
    {
        get
        {
            lock (_lock) { return _messages.Count; }
        }
    }
}
