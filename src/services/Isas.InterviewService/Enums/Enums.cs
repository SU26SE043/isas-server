namespace Isas.InterviewService.Enums

{
    public static class JobCategory
    {
        public const string BA = "BA";
        public const string BE = "BE";
        public const string FE = "FE";
    }

    public static class SessionStatus
    {
        public const string Draft       = "draft";
        public const string InProgress  = "in_progress";
        public const string Submitted   = "submitted";
        public const string Scored      = "scored";
        public const string Failed      = "failed";
        public const string Abandoned   = "abandoned";
    }

    public static class AnswerType
    {
        public const string Text  = "text";
        public const string Audio = "audio";
    }

    public static class ParseStatus
    {
        public const string Pending = "pending";
        public const string Done    = "done";
        public const string Failed  = "failed";
    }
}