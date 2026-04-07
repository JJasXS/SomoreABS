## Legacy Implementation Archive

The old appointment controller implementation was removed from active source
to avoid accidental routing or security drift.

Archived item:
- `Controllers/AppointmentController.old.cs` (removed from active tree)

Recovery:
- Use Git history if you need to inspect or restore old logic.

Reason:
- Reduce routing risk.
- Prevent duplicate implementation maintenance.
- Keep only hardened, current controller code in active build paths.
