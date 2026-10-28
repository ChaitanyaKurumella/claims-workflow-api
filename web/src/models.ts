export interface Claim {
  id: string;
  reference: string;
  amount: number;
  status: string;
  category: string;
  description: string;
  priority: string;
  assignee: string | null;
  version: number;
  createdAt: string;
  dueAt: string;
  escalated: boolean;
}
export interface Audit {
  sequence: number;
  at: string;
  action: string;
  actor: string;
  note: string;
  version: number;
}
export interface OutboxEvent {
  id: number;
  claimId: string;
  type: string;
  payload: string;
  attempts: number;
  status: string;
  nextAttempt: string;
  lastError: string | null;
}
export interface Operations {
  demoAvailable: boolean;
  deliveryMode: string;
  summary: { total: number; exposure: number; overdue: number };
  pendingEvents: number;
  deadLetters: number;
  events: OutboxEvent[];
}
export interface Session {
  id: string;
  role: string;
}
export interface ClaimPage {
  items: Claim[];
  nextCursor: string | null;
}
