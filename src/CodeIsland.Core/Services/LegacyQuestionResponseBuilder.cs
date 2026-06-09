using System.Text.Json.Nodes;
using CodeIsland.Core.Models;

namespace CodeIsland.Core.Services;

internal static class LegacyQuestionResponseBuilder
{
    public static string BuildQuestionAnswerResponse(
        QuestionData question,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        if (answers.Count > 1 || question.IsMultiQuestion)
        {
            var answerObject = new JsonObject();
            foreach (var answer in answers)
                answerObject[answer.Key] = JoinAnswers(answer.Value);
            return new JsonObject { ["answers"] = answerObject }.ToJsonString();
        }

        return new JsonObject { ["answer"] = answers.Values.Select(JoinAnswers).FirstOrDefault() ?? "" }.ToJsonString();
    }

    public static string BuildQuestionDismissResponse(string reason) =>
        new JsonObject
        {
            ["decision"] = "dismiss",
            ["allow"] = false,
            ["reason"] = reason
        }.ToJsonString();

    private static string JoinAnswers(IReadOnlyList<string> answers) => string.Join(", ", answers);
}
