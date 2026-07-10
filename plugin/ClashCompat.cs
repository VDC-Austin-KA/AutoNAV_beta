using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;

namespace AutoNAVMCP
{
    // Compatibility shim over Navisworks Clash API differences, 2024 - 2027.
    // (Carried over from AutoNAV2's ClashCompat and extended with the
    // result-edit operations the MCP bridge needs.)
    //
    //   * 2024 / 2025: ClashResult.AssignedTo / ApprovedBy are strings;
    //     TestsEditResultAssignedTo takes a string; TestsEditResultStatus
    //     takes (result, status); there is no ResolvedBy / ResolvedTime.
    //   * 2026 / 2027: AssignedTo / ApprovedBy / ResolvedBy are Assignee
    //     objects; TestsEditResultAssignedTo takes an Assignee;
    //     TestsEditResultStatus takes (result, status, currentUser).
    //   * 2024 / 2025 / 2026: DocumentClashTests exposes Tests and the
    //     single-arg TestsAddCopy / two-arg TestsReplaceWithCopy.
    //   * 2027: Tests was removed in favour of Value.TestsRoot.Children;
    //     AddCopy and ReplaceWithCopy require an explicit parent GroupItem.
    //
    // Code paths switch at compile time via the NW2024 / NW2025 / NW2026 /
    // NW2027 DefineConstants set from the NWYear build property.
    internal static class ClashCompat
    {
        public static IList<SavedItem> GetTopLevelTests(DocumentClashTests dct)
        {
#if NW2027
            return dct.Value.TestsRoot.Children;
#else
            return dct.Tests;
#endif
        }

        public static IEnumerable<ClashTest> EnumerateTests(DocumentClashTests dct)
        {
            return GetTopLevelTests(dct).OfType<ClashTest>();
        }

        public static int IndexOfTest(DocumentClashTests dct, SavedItem item)
        {
            return GetTopLevelTests(dct).IndexOf(item);
        }

        public static SavedItem TestAt(DocumentClashTests dct, int index)
        {
            return GetTopLevelTests(dct)[index];
        }

        public static void TestsAddCopyAtRoot(DocumentClashTests dct, ClashTest test)
        {
#if NW2027
            dct.TestsAddCopy(dct.Value.TestsRoot, test);
#else
            dct.TestsAddCopy(test);
#endif
        }

        public static void TestsReplaceAtRoot(DocumentClashTests dct, int index, ClashTest test)
        {
#if NW2027
            dct.TestsReplaceWithCopy(dct.Value.TestsRoot, index, test);
#else
            dct.TestsReplaceWithCopy(index, test);
#endif
        }

        public static void TestsRemoveAtRoot(DocumentClashTests dct, ClashTest test)
        {
#if NW2027
            dct.TestsRemove(dct.Value.TestsRoot, test);
#else
            dct.TestsRemove(test);
#endif
        }

        public static string GetAssignedTo(ClashResult result)
        {
#if NW2024 || NW2025
            return result.AssignedTo ?? "";
#else
            return result.AssignedTo != null ? (result.AssignedTo.DisplayName ?? "") : "";
#endif
        }

        public static string GetApprovedBy(ClashResult result)
        {
#if NW2024 || NW2025
            return result.ApprovedBy ?? "";
#else
            return result.ApprovedBy != null ? (result.ApprovedBy.DisplayName ?? "") : "";
#endif
        }

        public static string GetResolvedBy(ClashResult result)
        {
#if NW2024 || NW2025
            return "";
#else
            return result.ResolvedBy != null ? (result.ResolvedBy.DisplayName ?? "") : "";
#endif
        }

        public static DateTime? GetResolvedTime(ClashResult result)
        {
#if NW2024 || NW2025
            return null;
#else
            return result.ResolvedTime;
#endif
        }

        // Assign (or unassign, when assignedTo is null/empty) a clash result.
        public static void EditResultAssignedTo(DocumentClashTests dct, ClashResult result, string assignedTo)
        {
#if NW2024 || NW2025
            dct.TestsEditResultAssignedTo(result, assignedTo ?? "");
#else
            Assignee assignee = string.IsNullOrEmpty(assignedTo) ? new Assignee() : new Assignee(assignedTo);
            dct.TestsEditResultAssignedTo(result, assignee);
#endif
        }

        // Change a clash result's status. currentUser is recorded by 2026+ as
        // the acting user (Approved by / Resolved by).
        public static void EditResultStatus(DocumentClashTests dct, ClashResult result, ClashResultStatus status, string currentUser)
        {
#if NW2024 || NW2025
            dct.TestsEditResultStatus(result, status);
#else
            Assignee user = string.IsNullOrEmpty(currentUser) ? new Assignee(Environment.UserName) : new Assignee(currentUser);
            dct.TestsEditResultStatus(result, status, user);
#endif
        }

        // 2027 changed Comment.Author from string to Assignee.
        public static string GetCommentAuthor(Comment comment)
        {
#if NW2027
            return comment.Author != null ? (comment.Author.DisplayName ?? "") : "";
#else
            return comment.Author ?? "";
#endif
        }

        private static Comment MakeComment(string body, string author)
        {
            string name = string.IsNullOrEmpty(author) ? Environment.UserName : author;
#if NW2027
            return new Comment(body ?? "", CommentStatus.New, new Assignee(name));
#else
            return new Comment(body ?? "", CommentStatus.New, name);
#endif
        }

        // Append a comment to a clash result (comments are replaced as a
        // whole collection through TestsEditResultComments in every year).
        public static void AddResultComment(DocumentClashTests dct, ClashResult result, string body, string author)
        {
            var comments = new CommentCollection();
            if (result.Comments != null)
                foreach (Comment c in result.Comments) comments.Add(new Comment(c));
            comments.Add(MakeComment(body, author));
            dct.TestsEditResultComments(result, comments);
        }
    }
}
