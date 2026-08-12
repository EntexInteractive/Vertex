namespace Entex.Shared.Events
{
    public class LoggedEventArgs
    {
        public string? Message { get; }
        public bool? IsFatal { get; }

        public LoggedEventArgs(object message)
        {
            Message = message.ToString();
        }

        public LoggedEventArgs(object message, bool fatal)
        {
            Message = message.ToString();
            IsFatal = fatal;
        }
    }
}
