namespace MessageLink.Core.Services;

// Opens a cell the Messenger helper sealed with the platform's own protection. Not IStateProtector: that is the app's
// format for its own files, this is the helper's, which the app only ever reads.
public interface IHelperCellUnprotector
{
    // The value the helper sealed, with any format byte of the platform's own checked and taken off, or null for a cell
    // that does not open here.
    byte[]? Open(byte[] cell);
}
