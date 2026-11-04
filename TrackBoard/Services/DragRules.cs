using System.Linq.Expressions;
using TrackBoard.Entities;

namespace TrackBoard.Services;

/// <summary>
/// The single definition of which drag results reach a board. The SQL form and the in-memory
/// form below must stay equivalent; they sit side by side so they change together.
/// </summary>
/// <remarks>
/// Like lap times, these are trusted as uploaded. The app drops runs with a GPS gap or a clear
/// elevation drop before they get here. A result needs a car: without one it has no class and
/// nothing to show on the board.
/// </remarks>
public static class DragRules
{
    /// <summary>For queries: translated to SQL.</summary>
    public static readonly Expression<Func<DragResult, bool>> CountsForBoard = r =>
        r.Session.Visibility == SessionVisibility.Ranked
        && !r.Session.Voided
        && r.Session.VehicleId != null;

    /// <summary>For objects already in memory.</summary>
    public static bool Counts(Session session) =>
        session.Visibility == SessionVisibility.Ranked
        && !session.Voided
        && session.VehicleId != null;

    /// <summary>
    /// The class of a car, from horsepower per tonne. Null when the garage entry has no usable
    /// power or weight: such a car ranks overall but in no class.
    /// </summary>
    public static DragClass? ClassOf(int horsepower, double weightKg)
    {
        if (horsepower <= 0 || weightKg <= 0)
        {
            return null;
        }

        return (horsepower / (weightKg / 1000)) switch
        {
            < 100 => DragClass.D,
            < 150 => DragClass.C,
            < 250 => DragClass.B,
            < 400 => DragClass.A,
            _ => DragClass.S,
        };
    }
}
