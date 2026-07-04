using Microsoft.EntityFrameworkCore;
using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace E.Attendance.Data.Production;

public sealed class TimetableRepository : ITimetableRepository
{
    private readonly PbAttendanceDbContext _db;

    public TimetableRepository(PbAttendanceDbContext db)
    {
        _db = db;
    }

    public Task<TimetableEntity?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
        => _db.Timetables.Include(x => x.Days).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<TimetableEntity?> GetByNameAsync(string timetableName, CancellationToken cancellationToken = default)
        => _db.Timetables.Include(x => x.Days).FirstOrDefaultAsync(x => x.TimetableName == timetableName, cancellationToken);

    public async Task<IReadOnlyList<TimetableEntity>> GetAllAsync(CancellationToken cancellationToken = default)
        => await _db.Timetables.Include(x => x.Days).ToListAsync(cancellationToken);

    public async Task<TimetableEntity> AddAsync(TimetableEntity timetable, CancellationToken cancellationToken = default)
    {
        _db.Timetables.Add(timetable);
        await _db.SaveChangesAsync(cancellationToken);
        return timetable;
    }

    public async Task UpdateAsync(TimetableEntity timetable, CancellationToken cancellationToken = default)
    {
        _db.Timetables.Update(timetable);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var timetable = await GetByIdAsync(id, cancellationToken);
        if (timetable != null)
        {
            _db.Timetables.Remove(timetable);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public Task<TimetableDayEntity?> GetTimetableDayAsync(int timetableId, int dayOfWeek, CancellationToken cancellationToken = default)
        => _db.TimetableDays.FirstOrDefaultAsync(x => x.TimetableId == timetableId && x.DayOfWeek == dayOfWeek, cancellationToken);

    public async Task<IReadOnlyList<TimetableDayEntity>> GetTimetableDaysAsync(int timetableId, CancellationToken cancellationToken = default)
        => await _db.TimetableDays.Where(x => x.TimetableId == timetableId).ToListAsync(cancellationToken);

    public async Task<TimetableDayEntity> AddTimetableDayAsync(TimetableDayEntity timetableDay, CancellationToken cancellationToken = default)
    {
        _db.TimetableDays.Add(timetableDay);
        await _db.SaveChangesAsync(cancellationToken);
        return timetableDay;
    }

    public async Task UpdateTimetableDayAsync(TimetableDayEntity timetableDay, CancellationToken cancellationToken = default)
    {
        _db.TimetableDays.Update(timetableDay);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteTimetableDayAsync(int timetableId, int dayOfWeek, CancellationToken cancellationToken = default)
    {
        var timetableDay = await GetTimetableDayAsync(timetableId, dayOfWeek, cancellationToken);
        if (timetableDay != null)
        {
            _db.TimetableDays.Remove(timetableDay);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public Task<EmployeeTimetableEntity?> GetEmployeeTimetableAsync(Guid personnelId, CancellationToken cancellationToken = default)
        => _db.EmployeeTimetables.FirstOrDefaultAsync(x => x.PersonnelId == personnelId, cancellationToken);

    public async Task<IReadOnlyList<EmployeeTimetableEntity>> GetEmployeeTimetablesAsync(CancellationToken cancellationToken = default)
        => await _db.EmployeeTimetables.ToListAsync(cancellationToken);

    public async Task<EmployeeTimetableEntity> AddEmployeeTimetableAsync(EmployeeTimetableEntity employeeTimetable, CancellationToken cancellationToken = default)
    {
        _db.EmployeeTimetables.Add(employeeTimetable);
        await _db.SaveChangesAsync(cancellationToken);
        return employeeTimetable;
    }

    public async Task UpdateEmployeeTimetableAsync(EmployeeTimetableEntity employeeTimetable, CancellationToken cancellationToken = default)
    {
        _db.EmployeeTimetables.Update(employeeTimetable);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteEmployeeTimetableAsync(Guid personnelId, CancellationToken cancellationToken = default)
    {
        var employeeTimetable = await GetEmployeeTimetableAsync(personnelId, cancellationToken);
        if (employeeTimetable != null)
        {
            _db.EmployeeTimetables.Remove(employeeTimetable);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public Task<DepartmentTimetableEntity?> GetDepartmentTimetableAsync(int departmentId, CancellationToken cancellationToken = default)
        => _db.DepartmentTimetables.FirstOrDefaultAsync(x => x.DepartmentId == departmentId, cancellationToken);

    public async Task<IReadOnlyList<DepartmentTimetableEntity>> GetDepartmentTimetablesAsync(CancellationToken cancellationToken = default)
        => await _db.DepartmentTimetables.ToListAsync(cancellationToken);

    public async Task<DepartmentTimetableEntity> AddDepartmentTimetableAsync(DepartmentTimetableEntity departmentTimetable, CancellationToken cancellationToken = default)
    {
        _db.DepartmentTimetables.Add(departmentTimetable);
        await _db.SaveChangesAsync(cancellationToken);
        return departmentTimetable;
    }

    public async Task UpdateDepartmentTimetableAsync(DepartmentTimetableEntity departmentTimetable, CancellationToken cancellationToken = default)
    {
        _db.DepartmentTimetables.Update(departmentTimetable);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteDepartmentTimetableAsync(int departmentId, CancellationToken cancellationToken = default)
    {
        var departmentTimetable = await GetDepartmentTimetableAsync(departmentId, cancellationToken);
        if (departmentTimetable != null)
        {
            _db.DepartmentTimetables.Remove(departmentTimetable);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}