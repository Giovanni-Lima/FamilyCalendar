namespace FamilyCalendar.Services.Assistant;

/// <summary>Corpo di <c>POST /api/assistant/ask</c>.</summary>
public sealed record AskRequest(string? Question);

/// <summary>
/// Un'attività su cui si basa la risposta, citata nel testo come <c>[Ref]</c>.
/// <c>Date</c> = yyyy-MM-dd; <c>Time</c> = HH:mm o stringa vuota se tutto il giorno; <c>Place</c> = indirizzo o vuoto;
/// <c>Text</c> = nota troncata (~200 caratteri) o vuoto.
/// </summary>
public sealed record AssistantSourceDto(int Ref, int Id, string Date, string Time, string Title, string Place, string Text);

/// <summary>
/// Risposta dell'assistente. <c>Sources</c> contiene solo le attività citate con <c>[n]</c>.
/// <c>ReducedModel</c> è true se la risposta non viene dal primo modello Gemini; <c>Retrieved</c> = attività passate al modello.
/// </summary>
public sealed record AssistantAnswerDto(
    string Answer, List<AssistantSourceDto> Sources, string Model, bool ReducedModel, int Retrieved);
