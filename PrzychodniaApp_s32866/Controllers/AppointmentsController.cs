using Microsoft.AspNetCore.Mvc;
using System.Data;
using PrzychodniaApp_s32866.DTOs;
using Microsoft.Data.SqlClient;

namespace PrzychodniaApp_s32866.Controllers;

[ApiController]
[Route("api/appointments")]
public class AppointmentsController : ControllerBase
{
    private readonly string _connectionString;
    
    public AppointmentsController(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")!;
    }
    
    // GET /api/appointments?status=Scheduled&patientLastName=Kowalska
    [HttpGet]
    public async Task<IActionResult> GetAppointments(
        [FromQuery] string? status,
        [FromQuery] string? patientLastName)
    {
        var result = new List<AppointmentListDto>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("""
                                                 SELECT
                                                     a.IdAppointment,
                                                     a.AppointmentDate,
                                                     a.Status,
                                                     a.Reason,
                                                     p.FirstName + N' ' + p.LastName AS PatientFullName,
                                                     p.Email AS PatientEmail
                                                 FROM dbo.Appointments a
                                                 JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
                                                 WHERE (@Status IS NULL OR a.Status = @Status)
                                                   AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
                                                 ORDER BY a.AppointmentDate;
                                                 """, connection);

        command.Parameters.Add("@Status", SqlDbType.NVarChar).Value = 
            (object?)status ?? DBNull.Value;
        command.Parameters.Add("@PatientLastName", SqlDbType.NVarChar).Value = 
            (object?)patientLastName ?? DBNull.Value;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new AppointmentListDto
            {
                IdAppointment = reader.GetInt32(0),
                AppointmentDate = reader.GetDateTime(1),
                Status = reader.GetString(2),
                Reason = reader.GetString(3),
                PatientFullName = reader.GetString(4),
                PatientEmail = reader.GetString(5)
            });
        }

        return Ok(result);
    }
    
    // GET /api/appointments/{idAppointment}
    [HttpGet("{idAppointment}")]
    public async Task<IActionResult> GetAppointmentById(int idAppointment)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("""
                                                 SELECT
                                                     a.IdAppointment,
                                                     a.AppointmentDate,
                                                     a.Status,
                                                     a.Reason,
                                                     a.InternalNotes,
                                                     a.CreatedAt,
                                                     p.FirstName + N' ' + p.LastName AS PatientFullName,
                                                     p.Email AS PatientEmail,
                                                     d.FirstName + N' ' + d.LastName AS DoctorFullName,
                                                     d.LicenseNumber,
                                                     s.Name AS Specialization
                                                 FROM dbo.Appointments a
                                                 JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
                                                 JOIN dbo.Doctors d ON d.IdDoctor = a.IdDoctor
                                                 JOIN dbo.Specializations s ON s.IdSpecialization = d.IdSpecialization
                                                 WHERE a.IdAppointment = @IdAppointment;
                                                 """, connection);

        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
            return NotFound(new ErrorResponseDto { Message = "Appointment not found." });

        var dto = new AppointmentDetailsDto
        {
            IdAppointment = reader.GetInt32(0),
            AppointmentDate = reader.GetDateTime(1),
            Status = reader.GetString(2),
            Reason = reader.GetString(3),
            InternalNotes = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = reader.GetDateTime(5),
            PatientFullName = reader.GetString(6),
            PatientEmail = reader.GetString(7),
            DoctorFullName = reader.GetString(8),
            LicenseNumber = reader.GetString(9),
            Specialization = reader.GetString(10)
        };

        return Ok(dto);
    }
    
    // POST /api/appointments
    [HttpPost]
    public async Task<IActionResult> CreateAppointment([FromBody] CreateAppointmentRequestDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Reason) || dto.Reason.Length > 250)
            return BadRequest(new ErrorResponseDto { Message = "Reason is required and must be at most 250 characters." });

        if (dto.AppointmentDate <= DateTime.Now)
            return BadRequest(new ErrorResponseDto { Message = "Appointment date must be in the future." });

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var patientCmd = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.Patients WHERE IdPatient = @IdPatient AND IsActive = 1;", connection);
        patientCmd.Parameters.Add("@IdPatient", SqlDbType.Int).Value = dto.IdPatient;
        var patientExists = (int)await patientCmd.ExecuteScalarAsync()! > 0;
        if (!patientExists)
            return BadRequest(new ErrorResponseDto { Message = "Patient does not exist or is not active." });

        await using var doctorCmd = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.Doctors WHERE IdDoctor = @IdDoctor AND IsActive = 1;", connection);
        doctorCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = dto.IdDoctor;
        var doctorExists = (int)await doctorCmd.ExecuteScalarAsync()! > 0;
        if (!doctorExists)
            return BadRequest(new ErrorResponseDto { Message = "Doctor does not exist or is not active." });

        await using var conflictCmd = new SqlCommand("""
            SELECT COUNT(1) FROM dbo.Appointments
            WHERE IdDoctor = @IdDoctor
              AND AppointmentDate = @AppointmentDate
              AND Status = 'Scheduled';
            """, connection);
        conflictCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = dto.IdDoctor;
        conflictCmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime).Value = dto.AppointmentDate;
        var conflict = (int)await conflictCmd.ExecuteScalarAsync()! > 0;
        if (conflict)
            return Conflict(new ErrorResponseDto { Message = "Doctor already has an appointment at this time." });

        await using var insertCmd = new SqlCommand("""
            INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason)
            OUTPUT INSERTED.IdAppointment
            VALUES (@IdPatient, @IdDoctor, @AppointmentDate, 'Scheduled', @Reason);
            """, connection);
        insertCmd.Parameters.Add("@IdPatient", SqlDbType.Int).Value = dto.IdPatient;
        insertCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = dto.IdDoctor;
        insertCmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime).Value = dto.AppointmentDate;
        insertCmd.Parameters.Add("@Reason", SqlDbType.NVarChar).Value = dto.Reason;

        var newId = (int)await insertCmd.ExecuteScalarAsync()!;

        return CreatedAtAction(nameof(GetAppointmentById), new { idAppointment = newId }, new { IdAppointment = newId });
    }
    
    // PUT /api/appointments/{idAppointment}
    [HttpPut("{idAppointment}")]
    public async Task<IActionResult> UpdateAppointment(int idAppointment, [FromBody] UpdateAppointmentRequestDto dto)
    {
        var validStatuses = new[] { "Scheduled", "Completed", "Cancelled" };
        if (!validStatuses.Contains(dto.Status))
            return BadRequest(new ErrorResponseDto { Message = "Invalid status. Must be Scheduled, Completed or Cancelled." });

        if (string.IsNullOrWhiteSpace(dto.Reason) || dto.Reason.Length > 250)
            return BadRequest(new ErrorResponseDto { Message = "Reason is required and must be at most 250 characters." });

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var appointmentCmd = new SqlCommand(
            "SELECT Status, AppointmentDate FROM dbo.Appointments WHERE IdAppointment = @IdAppointment;", connection);
        appointmentCmd.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        await using var appointmentReader = await appointmentCmd.ExecuteReaderAsync();
        if (!await appointmentReader.ReadAsync())
            return NotFound(new ErrorResponseDto { Message = "Appointment not found." });

        var currentStatus = appointmentReader.GetString(0);
        var currentDate = appointmentReader.GetDateTime(1);
        await appointmentReader.CloseAsync();

        if (currentStatus == "Completed" && dto.AppointmentDate != currentDate)
            return Conflict(new ErrorResponseDto { Message = "Cannot change date of a completed appointment." });

        await using var patientCmd = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.Patients WHERE IdPatient = @IdPatient AND IsActive = 1;", connection);
        patientCmd.Parameters.Add("@IdPatient", SqlDbType.Int).Value = dto.IdPatient;
        var patientExists = (int)await patientCmd.ExecuteScalarAsync()! > 0;
        if (!patientExists)
            return BadRequest(new ErrorResponseDto { Message = "Patient does not exist or is not active." });

        await using var doctorCmd = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.Doctors WHERE IdDoctor = @IdDoctor AND IsActive = 1;", connection);
        doctorCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = dto.IdDoctor;
        var doctorExists = (int)await doctorCmd.ExecuteScalarAsync()! > 0;
        if (!doctorExists)
            return BadRequest(new ErrorResponseDto { Message = "Doctor does not exist or is not active." });

        await using var conflictCmd = new SqlCommand("""
            SELECT COUNT(1) FROM dbo.Appointments
            WHERE IdDoctor = @IdDoctor
              AND AppointmentDate = @AppointmentDate
              AND Status = 'Scheduled'
              AND IdAppointment != @IdAppointment;
            """, connection);
        conflictCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = dto.IdDoctor;
        conflictCmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime).Value = dto.AppointmentDate;
        conflictCmd.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        var conflict = (int)await conflictCmd.ExecuteScalarAsync()! > 0;
        if (conflict)
            return Conflict(new ErrorResponseDto { Message = "Doctor already has an appointment at this time." });

        await using var updateCmd = new SqlCommand("""
            UPDATE dbo.Appointments
            SET IdPatient = @IdPatient,
                IdDoctor = @IdDoctor,
                AppointmentDate = @AppointmentDate,
                Status = @Status,
                Reason = @Reason,
                InternalNotes = @InternalNotes
            WHERE IdAppointment = @IdAppointment;
            """, connection);
        updateCmd.Parameters.Add("@IdPatient", SqlDbType.Int).Value = dto.IdPatient;
        updateCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = dto.IdDoctor;
        updateCmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime).Value = dto.AppointmentDate;
        updateCmd.Parameters.Add("@Status", SqlDbType.NVarChar).Value = dto.Status;
        updateCmd.Parameters.Add("@Reason", SqlDbType.NVarChar).Value = dto.Reason;
        updateCmd.Parameters.Add("@InternalNotes", SqlDbType.NVarChar).Value = (object?)dto.InternalNotes ?? DBNull.Value;
        updateCmd.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        await updateCmd.ExecuteNonQueryAsync();

        return Ok();
    }
    
    // DELETE /api/appointments/{idAppointment}
    [HttpDelete("{idAppointment}")]
    public async Task<IActionResult> DeleteAppointment(int idAppointment)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var checkCmd = new SqlCommand(
            "SELECT Status FROM dbo.Appointments WHERE IdAppointment = @IdAppointment;", connection);
        checkCmd.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        var status = (string?)await checkCmd.ExecuteScalarAsync();

        if (status == null)
            return NotFound(new ErrorResponseDto { Message = "Appointment not found." });

        if (status == "Completed")
            return Conflict(new ErrorResponseDto { Message = "Cannot delete a completed appointment." });

        await using var deleteCmd = new SqlCommand(
            "DELETE FROM dbo.Appointments WHERE IdAppointment = @IdAppointment;", connection);
        deleteCmd.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        await deleteCmd.ExecuteNonQueryAsync();

        return NoContent();
    }
}

