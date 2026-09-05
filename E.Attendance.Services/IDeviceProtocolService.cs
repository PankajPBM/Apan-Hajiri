using E.Attendance.Core.Models;

namespace E.Attendance.Services;

public interface IDeviceProtocolService
{
    string CreateUserInfoCommand(Employee employee, Device device);
    string CreateAccTimeZoneCommand(int timetableId, IEnumerable<(int DayOfWeek, TimeOnly Start, TimeOnly End)> dailySchedules);
    string CreateAccGroupCommand(object id, int timetableId);
    string CreateAccHolidayCommand(int holidayId, string holidayName, DateOnly startDate, DateOnly endDate, int timeZoneId);
    string CreateAccUnLockCombCommand(int combinationId, int[] groupIds);
    string CreateBlacklistCommand(string idNumber, string name, int gender, int nation);
    string CreateWhitelistCommand(string idNumber, string name, int gender, int nation, DateTime startDate, DateTime endDate);
    string CreateRemoteUnlockCommand();
    string CreateRemoteEnrollCommand(string pin, int type, int retry, int overwrite, string? cardNo);
    string CreateDeleteUserCommand(string pin);
    string CreateRebootCommand();
    
    IReadOnlyList<(SyncType type, string command)> CreateBulkSyncCommands(
        IEnumerable<Employee> employees, 
        Device device,
        IEnumerable<TimetableEntity> timetables,
        IEnumerable<DepartmentTimetableEntity> departmentTimetables,
        IEnumerable<HolidayEntity> holidays,
        IEnumerable<AccCombinationEntity> combinations);
}
