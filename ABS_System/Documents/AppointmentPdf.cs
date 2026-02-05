using System;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using YourApp.Models;

namespace YourApp.Documents
{
    public class AppointmentPdf : IDocument
    {
        private readonly Appointment _appt;
        private readonly byte[]? _signature;

        public AppointmentPdf(Appointment appt, byte[]? signature)
        {
            _appt = appt;
            _signature = signature;
        }

        public DocumentMetadata GetMetadata() => DocumentMetadata.Default;

        public void Compose(IDocumentContainer container)
        {
            const string reportTitle = "Appointment Acknowledgement";
            string apptIdText = _appt.ApptId.ToString();

            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontSize(11));

                // =========================
                // HEADER (single chain)
                // =========================
                page.Header().Element(header =>
                {
                    header.Column(h =>
                    {
                        h.Spacing(8);

                        h.Item().Row(row =>
                        {
                            row.RelativeItem().Column(col =>
                            {
                                col.Item().Text(reportTitle).FontSize(18).Bold();
                                col.Item().Text("E-Signature Summary Report")
                                    .FontSize(10)
                                    .FontColor(Colors.Grey.Darken2);
                            });

                            row.ConstantItem(160).AlignRight().Column(col =>
                            {
                                col.Item().Text("Appointment ID")
                                    .FontSize(9)
                                    .FontColor(Colors.Grey.Darken2);

                                col.Item().Text(apptIdText)
                                    .FontSize(14)
                                    .Bold();
                            });
                        });

                        h.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                    });
                });

                // =========================
                // CONTENT
                // =========================
                page.Content().PaddingTop(12).Column(col =>
                {
                    col.Spacing(16);

                    // --- Status banner ---
                    col.Item().Element(e =>
                    {
                        var status = (_appt.Status ?? "-").Trim();
                        var isFulfilled = status.Equals("FULFILLED", StringComparison.OrdinalIgnoreCase);

                        e.Border(1)
                         .BorderColor(Colors.Grey.Lighten2)
                         .Background(isFulfilled ? Colors.Green.Lighten5 : Colors.Orange.Lighten5)
                         .Padding(10)
                         .Row(r =>
                         {
                             r.RelativeItem().Column(c =>
                             {
                                 c.Item().Text("Status").FontSize(9).FontColor(Colors.Grey.Darken2);
                                 c.Item().Text(status).Bold().FontSize(12);
                             });

                             r.ConstantItem(220).AlignRight().Column(c =>
                             {
                                 c.Item().Text("Appointment Date/Time").FontSize(9).FontColor(Colors.Grey.Darken2);
                                 c.Item().Text($"{_appt.ApptStart:yyyy-MM-dd HH:mm}").Bold().FontSize(12);
                             });
                         });
                    });

                    // --- Details card (2 columns) ---
                    col.Item().Element(card =>
                    {
                        card.Border(1)
                            .BorderColor(Colors.Grey.Lighten2)
                            .Padding(14)
                            .Column(c =>
                            {
                                c.Spacing(10);

                                c.Item().Text("Appointment Details").Bold().FontSize(12);
                                c.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten2);

                                c.Item().Row(r =>
                                {
                                    r.Spacing(16);

                                    r.RelativeItem().Column(left =>
                                    {
                                        left.Spacing(8);
                                        Field(left, "Customer", string.IsNullOrWhiteSpace(_appt.CustomerName) ? _appt.CustomerCode : _appt.CustomerName);
                                        Field(left, "Agent", string.IsNullOrWhiteSpace(_appt.AgentName) ? _appt.AgentCode : _appt.AgentName);

                                        Field(left, "Title", _appt.Title);
                                    });

                                    r.RelativeItem().Column(right =>
                                    {
                                        right.Spacing(8);
                                        Field(right, "Appointment ID", _appt.ApptId.ToString());
                                        Field(right, "Date/Time", $"{_appt.ApptStart:yyyy-MM-dd HH:mm}");
                                        Field(right, "Status", _appt.Status);
                                    });
                                });
                            });
                    });

                    // --- Statement box ---
                    col.Item().Element(statement =>
                    {
                        var text = string.IsNullOrWhiteSpace(_appt.Notes) ? "-" : _appt.Notes.Trim();

                        statement.Border(1)
                                 .BorderColor(Colors.Grey.Lighten2)
                                 .Background(Colors.Grey.Lighten5)
                                 .Padding(14)
                                 .Column(c =>
                                 {
                                     c.Spacing(8);
                                     c.Item().Text("Statement").Bold().FontSize(12);
                                     c.Item().Text(text).FontSize(11);
                                 });
                    });

                    // --- Signature section ---
                    col.Item().Element(sig =>
                    {
                        bool hasSig = _signature != null && _signature.Length > 0;

                        sig.Border(1)
                           .BorderColor(Colors.Grey.Lighten2)
                           .Padding(14)
                           .Column(c =>
                           {
                               c.Spacing(10);

                               c.Item().Row(r =>
                               {
                                   r.RelativeItem().Text("Signature").Bold().FontSize(12);

                                   r.ConstantItem(140).AlignRight().Text(hasSig ? "SIGNED" : "NOT SIGNED")
                                       .FontSize(10)
                                       .SemiBold()
                                       .FontColor(hasSig ? Colors.Green.Darken2 : Colors.Grey.Darken2);
                               });

                               c.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten2);

                               if (hasSig)
                               {
                                   c.Item()
                                    .Height(130)
                                    .Border(1)
                                    .BorderColor(Colors.Grey.Lighten2)
                                    .Background(Colors.White)
                                    .Padding(10)
                                    .AlignMiddle()
                                    .AlignCenter()
                                    .Image(_signature!)
                                    .FitArea();
                               }
                               else
                               {
                                   c.Item().Text("No signature image found for this appointment.")
                                       .FontSize(10)
                                       .FontColor(Colors.Grey.Darken2);
                               }
                           });
                    });
                });

                // =========================
                // FOOTER (single chain)
                // =========================
                page.Footer().Element(footer =>
                {
                    footer.Column(f =>
                    {
                        f.Spacing(8);

                        f.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten2);

                        f.Item().Row(row =>
                        {
                            row.RelativeItem().Text(txt =>
                            {
                                txt.Span("Generated: ").FontSize(9).FontColor(Colors.Grey.Darken2);
                                txt.Span(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).FontSize(9);
                            });

                            row.ConstantItem(140).AlignRight().Text(txt =>
                            {
                                txt.Span("Page ").FontSize(9).FontColor(Colors.Grey.Darken2);
                                txt.CurrentPageNumber().FontSize(9);
                                txt.Span(" / ").FontSize(9).FontColor(Colors.Grey.Darken2);
                                txt.TotalPages().FontSize(9);
                            });
                        });
                    });
                });
            });
        }

        // Helper: consistent field rows
        private static void Field(ColumnDescriptor col, string label, string? value)
        {
            col.Item().Row(r =>
            {
                r.ConstantItem(110).Text(label)
                    .FontSize(9)
                    .FontColor(Colors.Grey.Darken2);

                r.RelativeItem().Text(string.IsNullOrWhiteSpace(value) ? "-" : value.Trim())
                    .FontSize(11)
                    .SemiBold();
            });
        }
    }
}
