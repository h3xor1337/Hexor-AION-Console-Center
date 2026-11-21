using Hexor;

internal static class DailySequenceIncidentTests
{
    public static void Run(Action<bool, string> assert)
    {
        assert(!DailySequenceIncidents.MentionsAuthorization("[]"), "An empty incident list does not mention authorization");
        assert(!DailySequenceIncidents.MentionsAuthorization("{\"incidents\":[]}"), "No incidents means no authorization problem");
        assert(DailySequenceIncidents.MentionsAuthorization("{\"kind\":\"auth\",\"error\":\"KEY_REPLACED\"}"), "KEY_REPLACED incidents are recognized");
        assert(DailySequenceIncidents.MentionsAuthorization("{\"message\":\"卡密已在其他机器登录，当前机器授权已失效\"}"), "Chinese authorization loss wording is recognized");
        assert(DailySequenceIncidents.MentionsAuthorization("{\"detail\":\"StateMachineManager skipped: auth_not_authorized\"}"), "The frozen-tick incident wording is recognized");
        assert(DailySequenceIncidents.MentionsAuthorization("{\"state\":\"SESSION_REVOKED\"}"), "Revoked sessions are recognized");
        assert(!DailySequenceIncidents.MentionsAuthorization("{\"kind\":\"dungeon\",\"stage\":\"boss\"}"), "Unrelated incidents do not trigger the authorization warning");
    }
}
