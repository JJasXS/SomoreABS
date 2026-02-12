using System;
using System.Collections.Generic;
using System.IO;
using FirebirdSql.Data.FirebirdClient;
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
            _appt = appt ?? throw new ArgumentNullException(nameof(appt));
            _signature = signature;
        }

        public DocumentMetadata GetMetadata() => DocumentMetadata.Default;

        /// <summary>
        /// Factory: Build AppointmentPdf from APPOINTMENT_LOG by LOG_ID (and ACTION_TYPE = 'ADDED')
        /// </summary>
        public static AppointmentPdf FromLogId(long logId, FbConnection conn)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));

            long apptId;
            string? details;
            int soQty, claimed, prevClaimed;
            string serviceCode;

            // 1) Read log row (except DETAILS)
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT
    APPT_ID,
    SO_QTY,
    CLAIMED,
    PREV_CLAIMED,
    SERVICE_CODE
FROM APPOINTMENT_LOG
WHERE LOG_ID = @LOGID
  AND ACTION_TYPE = 'ADDED'
";
                cmd.Parameters.Add(new FbParameter("@LOGID", logId));

                using var r = cmd.ExecuteReader();
                if (!r.Read())
                    throw new Exception("Log entry not found.");

                apptId = r.GetInt64(0);
                soQty = r.IsDBNull(1) ? 0 : r.GetInt32(1);
                claimed = r.IsDBNull(2) ? 0 : r.GetInt32(2);
                prevClaimed = r.IsDBNull(3) ? 0 : r.GetInt32(3);
                serviceCode = r.IsDBNull(4) ? "" : r.GetString(4);
            }

            // 1b) Fetch DETAILS from APPT_SIGNATURE.REMARKS
            using (var cmdRemarks = conn.CreateCommand())
            {
                cmdRemarks.CommandText = @"SELECT REMARKS FROM APPT_SIGNATURE WHERE APPT_ID = @APPTID";
                cmdRemarks.Parameters.Add(new FbParameter("@APPTID", apptId));
                using var rRemarks = cmdRemarks.ExecuteReader();
                details = (rRemarks.Read() && !rRemarks.IsDBNull(0)) ? rRemarks.GetString(0) : null;
            }

            // 2) Fetch appointment info and names
            string? customerCode = null, agentCode = null, title = null, customerName = null, agentName = null, serviceTitle = null;
            DateTime apptStart = default;
            string? status = null;

            using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = @"
SELECT
    a.CUSTOMER_CODE,
    a.AGENT_CODE,
    a.TITLE,
    a.APPT_START,
    a.STATUS,
    c.COMPANYNAME,
    ag.DESCRIPTION
FROM APPOINTMENT a
LEFT JOIN AR_CUSTOMER c ON a.CUSTOMER_CODE = c.CODE
LEFT JOIN AGENT ag ON a.AGENT_CODE = ag.CODE
WHERE a.APPT_ID = @APPTID
";
                cmd2.Parameters.Add(new FbParameter("@APPTID", apptId));

                using var r2 = cmd2.ExecuteReader();
                if (r2.Read())
                {
                    customerCode = r2.IsDBNull(0) ? null : r2.GetString(0);
                    agentCode = r2.IsDBNull(1) ? null : r2.GetString(1);
                    title = r2.IsDBNull(2) ? null : r2.GetString(2);
                    apptStart = r2.IsDBNull(3) ? default : r2.GetDateTime(3);
                    status = r2.IsDBNull(4) ? null : r2.GetString(4);
                    customerName = r2.IsDBNull(5) ? null : r2.GetString(5);
                    agentName = r2.IsDBNull(6) ? null : r2.GetString(6);
                }
            }

            // 2b) Fetch service title from ST_ITEM.DESCRIPTION
            if (!string.IsNullOrWhiteSpace(serviceCode))
            {
                using var cmdItem = conn.CreateCommand();
                cmdItem.CommandText = @"SELECT DESCRIPTION FROM ST_ITEM WHERE CODE = @CODE";
                cmdItem.Parameters.Add(new FbParameter("@CODE", serviceCode));
                using var rItem = cmdItem.ExecuteReader();
                if (rItem.Read() && !rItem.IsDBNull(0))
                {
                    serviceTitle = rItem.GetString(0);
                }
            }

            // 3) Load signature blob
            byte[]? signatureBytes = null;
            using (var cmdSig = conn.CreateCommand())
            {
                cmdSig.CommandText = @"
SELECT SIGNATURE_PNG
FROM APPT_SIGNATURE
WHERE APPT_ID = @APPTID
";
                cmdSig.Parameters.Add(new FbParameter("@APPTID", apptId));

                using var rSig = cmdSig.ExecuteReader();
                if (rSig.Read() && !rSig.IsDBNull(0))
                {
                    using var ms = new MemoryStream();
                    long offset = 0;
                    var buffer = new byte[8192];
                    long read;
                    while ((read = rSig.GetBytes(0, offset, buffer, 0, buffer.Length)) > 0)
                    {
                        ms.Write(buffer, 0, (int)read);
                        offset += read;
                    }
                    signatureBytes = ms.ToArray();
                }
            }

            // 4) Build model for PDF

            // Load all services for this appointment from APPT_DTL
            var services = new List<ApptDtl>();
            using (var cmdDtl = conn.CreateCommand())
            {
                cmdDtl.CommandText = @"
SELECT d.APPT_ID, d.SERVICE_CODE, COALESCE(s.QTY, 0) AS QTY, COALESCE(s.UDF_CLAIMED, 0) AS CLAIMED, COALESCE(s.UDF_PREV_CLAIMED, 0) AS PREV_CLAIMED
FROM APPT_DTL d
LEFT JOIN SL_SODTL s ON s.ITEMCODE = d.SERVICE_CODE
    AND s.DOCKEY IN (SELECT so.DOCKEY FROM SL_SO so WHERE so.CODE = @CUST)
WHERE d.APPT_ID = @APPTID
";
                cmdDtl.Parameters.Add(new FbParameter("@APPTID", apptId));
                cmdDtl.Parameters.Add(new FbParameter("@CUST", customerCode ?? string.Empty));
                using var rDtl = cmdDtl.ExecuteReader();
                while (rDtl.Read())
                {
                    services.Add(new ApptDtl
                    {
                        ApptId = rDtl.IsDBNull(0) ? 0 : rDtl.GetInt64(0),
                        ServiceCode = rDtl.IsDBNull(1) ? "" : rDtl.GetString(1),
                        Qty = rDtl.IsDBNull(2) ? 0 : rDtl.GetInt32(2),
                        Claimed = rDtl.IsDBNull(3) ? 0 : rDtl.GetInt32(3),
                        PrevClaimed = rDtl.IsDBNull(4) ? 0 : rDtl.GetInt32(4)
                    });
                }
            }

            var appt = new Appointment
            {
                ApptId = apptId,
                CustomerCode = customerCode ?? string.Empty,
                AgentCode = agentCode ?? string.Empty,
                Title = !string.IsNullOrWhiteSpace(serviceTitle) ? serviceTitle : title,
                Notes = details,
                ApptStart = apptStart,
                Status = status,
                CustomerName = string.IsNullOrWhiteSpace(customerName) ? customerCode : customerName,
                AgentName = string.IsNullOrWhiteSpace(agentName) ? agentCode : agentName,
                Services = services
            };

            return new AppointmentPdf(appt, signatureBytes);
        }

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

                            // Keep blank
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

                    // --- Status banner (Date/Time ONLY here) ---
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
                                 c.Item().Text("Appointment Date/Time")
                                     .FontSize(9)
                                     .FontColor(Colors.Grey.Darken2);

                                 var dtText = (_appt.ApptStart == default)
                                     ? "-"
                                     : _appt.ApptStart.ToString("yyyy-MM-dd HH:mm");

                                 c.Item().Text(dtText).Bold().FontSize(12);
                             });
                         });
                    });

                    // --- Details card ---
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

                                // --- Service Claims ---
                                if (_appt.Services != null && _appt.Services.Count > 0)
                                {
                                    c.Item().PaddingTop(10).Text("Service Claims").Bold().FontSize(11);

                                    c.Item().Table(table =>
                                    {
                                        table.ColumnsDefinition(cols =>
                                        {
                                            cols.RelativeColumn();
                                            cols.ConstantColumn(140);
                                        });

                                        table.Header(header =>
                                        {
                                            header.Cell().Text("Service Code").FontSize(9).Bold();
                                            header.Cell().AlignRight().Text("Claimed / Qty").FontSize(9).Bold();
                                        });

                                        foreach (var svc in _appt.Services)
                                        {
                                            var totalClaimed = svc.Claimed + svc.PrevClaimed;

                                            table.Cell().Text(string.IsNullOrWhiteSpace(svc.ServiceCode) ? "-" : svc.ServiceCode);
                                            table.Cell().AlignRight().Text($"{totalClaimed} / {svc.Qty}");
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

                    // --- Signature section (same style as your earlier version) ---
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
