using Microsoft.Extensions.Localization;
using OpenMod.Unturned.Users;

namespace SkaliaDeps
{
    internal static class PrivateMessageFormatter
    {
        public static string AppendNotice(
            IStringLocalizer stringLocalizer,
            string message)
        {
            return $"{message} {stringLocalizer["private_message_suffix"].Value}";
        }

        public static string AppendNoticeIfPlayer(
            IStringLocalizer stringLocalizer,
            string message,
            object actor)
        {
            return actor is UnturnedUser
                ? AppendNotice(stringLocalizer, message)
                : message;
        }
    }
}
