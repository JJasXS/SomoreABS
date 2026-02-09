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

            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontSize(11));

                // =========================
                // HEADER
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

                            // ✅ Appointment ID removed
                            row.ConstantItem(160).AlignRight().Text("");
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

                    // --- Status banner (KEEP Date/Time here) ---
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

                             // ✅ Date/Time stays ONLY here
                             r.ConstantItem(220).AlignRight().Column(c =>
                             {
                                 c.Item().Text("Appointment Date/Time")
                                     .FontSize(9)
                                     .FontColor(Colors.Grey.Darken2);

                                 c.Item().Text($"{_appt.ApptStart:yyyy-MM-dd HH:mm}")
                                     .Bold()
                                     .FontSize(12);
                             });
                         });
                    });

                    // --- Details card (NO Date/Time, NO Status) ---
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

                                c.Item().Column(one =>
                                {
                                    one.Spacing(8);

                                    Field(one, "Customer",
                                        string.IsNullOrWhiteSpace(_appt.CustomerName)
                                            ? _appt.CustomerCode
                                            : _appt.CustomerName);

                                    Field(one, "Agent",
                                        string.IsNullOrWhiteSpace(_appt.AgentName)
                                            ? _appt.AgentCode
                                            : _appt.AgentName);

                                    Field(one, "Title", _appt.Title);
                                });

                                // --- List services with CLAIMED info ---
                                if (_appt.Services != null && _appt.Services.Count > 0)
                                {
                                    c.Item().PaddingTop(10).Text("Service Claims").Bold().FontSize(11);
                                    c.Item().Table(table =>
                                    {
                                        table.ColumnsDefinition(cols =>
                                        {
                                            cols.ConstantColumn(120);
                                            cols.ConstantColumn(120);
                                        });
                                        table.Header(header =>
                                        {
                                            header.Cell().Text("Service Code").FontSize(9).Bold();
                                            header.Cell().Text("Claimed / Qty").FontSize(9).Bold();
                                        });
                                        foreach (var svc in _appt.Services)
                                        {
                                            var totalClaimed = svc.Claimed + svc.PrevClaimed;
                                            table.Cell().Text(svc.ServiceCode);
                                            table.Cell().Text($"{totalClaimed} / {svc.Qty}");
                                        }
                                    });
                                }
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

                    // --- Signature section (small, bottom-right) ---
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
                                    .AlignRight()
                                    .Width(220)
                                    .Height(80)
                                    .Border(1)
                                    .BorderColor(Colors.Grey.Lighten2)
                                    .Background(Colors.White)
                                    .Padding(6)
                                    .AlignBottom()
                                    .AlignRight()
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
                // FOOTER
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
