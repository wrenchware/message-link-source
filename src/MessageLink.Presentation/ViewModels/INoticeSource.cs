namespace MessageLink.Presentation.ViewModels;

/// <summary>
/// Something that reports a line of feedback into the conversation's notice bar.
/// <para>
/// The bar is for things the person has to do something about. Progress and success are not: a change that worked is
/// announced by the conversation changing, and a bar saying so is noise that then has to be dismissed by hand. Those
/// are marked advisory and never occupy it.
/// </para>
/// <para>
/// The default is deliberately the other way round — anything set without saying it is advisory is shown. A new
/// message that nobody classified is then merely noisy, rather than a warning that disappears silently. This replaced
/// a list of English prefixes in the view, which every new message had to be remembered and added to; one was not,
/// which is how "Change accepted. Waiting for the conversation to update." came to sit in the conversation after the
/// change had already landed.
/// </para>
/// </summary>
public interface INoticeSource
{
    string Status { get; }
    bool StatusIsAdvisory { get; }
}
