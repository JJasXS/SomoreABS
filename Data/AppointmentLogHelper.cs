using System;
using System.Collections.Generic;
using System.Linq;
using FirebirdSql.Data.FirebirdClient;
using YourApp.Models;

namespace YourApp.Data
{
    public static class AppointmentLogHelper
    {
        // Returns the latest LOG_ID for a given APPT_ID and ACTION_TYPE = 'ADDED'
        public static long? GetLatestLogIdForAppointment(long apptId, FbConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT LOG_ID FROM APPOINTMENT_LOG WHERE APPT_ID = @APPTID AND ACTION_TYPE = 'ADDED' ORDER BY ACTION_TIME DESC ROWS 1";
            cmd.Parameters.Add(new FbParameter("@APPTID", apptId));
            var result = cmd.ExecuteScalar();
            if (result == null || result == DBNull.Value) return null;
            return Convert.ToInt64(result);
        }
    }
}
