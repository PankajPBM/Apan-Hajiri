using E.Attendance.Core.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace E.Attendance.Core.Interfaces;

public interface ITimetableRepository
{
    Task<TimetableEntity?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<TimetableEntity?> GetByNameAsync(string timetableName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TimetableEntity>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<TimetableEntity> AddAsync(TimetableEntity timetable, CancellationToken cancellationToken = default);
    Task UpdateAsync(TimetableEntity timetable, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    
    Task<TimetableDayEntity?> GetTimetableDayAsync(int timetableId, int dayOfWeek, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TimetableDayEntity>> GetTimetableDaysAsync(int timetableId, CancellationToken cancellationToken = default);
    Task<TimetableDayEntity> AddTimetableDayAsync(TimetableDayEntity timetableDay, CancellationToken cancellationToken = default);
    Task UpdateTimetableDayAsync(TimetableDayEntity timetableDay, CancellationToken cancellationToken = default);
    Task DeleteTimetableDayAsync(int timetableId, int dayOfWeek, CancellationToken cancellationToken = default);
    
    Task<EmployeeTimetableEntity?> GetEmployeeTimetableAsync(Guid personnelId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EmployeeTimetableEntity>> GetEmployeeTimetablesAsync(CancellationToken cancellationToken = default);
    Task<EmployeeTimetableEntity> AddEmployeeTimetableAsync(EmployeeTimetableEntity employeeTimetable, CancellationToken cancellationToken = default);
    Task UpdateEmployeeTimetableAsync(EmployeeTimetableEntity employeeTimetable, CancellationToken cancellationToken = default);
    Task DeleteEmployeeTimetableAsync(Guid personnelId, CancellationToken cancellationToken = default);
    
    Task<DepartmentTimetableEntity?> GetDepartmentTimetableAsync(int departmentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DepartmentTimetableEntity>> GetDepartmentTimetablesAsync(CancellationToken cancellationToken = default);
    Task<DepartmentTimetableEntity> AddDepartmentTimetableAsync(DepartmentTimetableEntity departmentTimetable, CancellationToken cancellationToken = default);
    Task UpdateDepartmentTimetableAsync(DepartmentTimetableEntity departmentTimetable, CancellationToken cancellationToken = default);
    Task DeleteDepartmentTimetableAsync(int departmentId, CancellationToken cancellationToken = default);
}