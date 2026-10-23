import { Claim } from "./models";
export function label(s: string) {
  return (
    (
      {
        All: "All claims",
        Submitted: "Intake",
        InReview: "In review",
        PendingSupervisor: "Supervisor review",
        Approved: "Approved",
        Rejected: "Rejected",
        SupervisorRequested: "Supervisor requested",
        SlaEscalated: "SLA escalated",
        assign: "Assigned",
        release: "Assignment released",
        approve: "Approved",
        reject: "Rejected",
      } as Record<string, string>
    )[s] || s
  );
}
export function isFinal(c: Claim) {
  return ["Approved", "Rejected"].includes(c.status);
}
export function canDecide(c: Claim, actor: string, role: string) {
  return (
    !!actor &&
    ((c.status === "InReview" && c.assignee === actor) ||
      (c.status === "PendingSupervisor" &&
        ["Supervisor", "Administrator"].includes(role) &&
        c.assignee !== actor))
  );
}
