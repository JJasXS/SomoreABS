using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using YourApp.Models;
using System.IO;

public class AppointmentPdf : IDocument
{
    private readonly Appointment _appt;
    private readonly byte[] _signature;

    public AppointmentPdf(Appointment appt, byte[] signature)
    {
        _appt = appt;
        _signature = signature;
    }

    public DocumentMetadata GetMetadata() => DocumentMetadata.Default;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(40);
            page.DefaultTextStyle(x => x.FontSize(12));

            page.Content().Column(col =>
            {
                col.Spacing(15);

                col.Item().Text("Appointment Report")
                    .FontSize(20).Bold();

                col.Item().LineHorizontal(1);

                col.Item().Text($"ID: {_appt.ApptId}");
                col.Item().Text($"Date: {_appt.ApptStart:yyyy-MM-dd HH:mm}");
                col.Item().Text($"Status: {_appt.Status}");

                col.Item().PaddingTop(10).Text("Statement").Bold();
                col.Item().Border(1).Padding(10)
                    .Text(_appt.Notes ?? "-");

                if (_signature != null && _signature.Length > 0)
{
    col.Item().PaddingTop(20).Text("Signature").Bold();

    col.Item().Height(120).Image(_signature);
}


                col.Item().PaddingTop(20)
                    .Text($"Signed By: {_appt.CustomerCode}");
            });
        });
    }
}
