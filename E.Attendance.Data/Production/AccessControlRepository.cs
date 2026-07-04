using Microsoft.EntityFrameworkCore;
using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace E.Attendance.Data.Production;

public sealed class AccessControlRepository(PbAttendanceDbContext db) : IHolidayRepository, IAccCombinationRepository
{
    public async Task<IReadOnlyList<HolidayEntity>> GetAllAsync(CancellationToken ct = default)
        => await db.Holidays.Where(x => x.IsActive).ToListAsync(ct);

    async Task<IReadOnlyList<AccCombinationEntity>> IAccCombinationRepository.GetAllAsync(CancellationToken ct)
        => await db.AccCombinations.Where(x => x.IsActive).ToListAsync(ct);
}
