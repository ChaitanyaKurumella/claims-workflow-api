import { Component, Input, Output, EventEmitter } from "@angular/core";
import { CommonModule } from "@angular/common";
import { FormsModule } from "@angular/forms";
import { Claim, Audit } from "./models";
import { label, isFinal, canDecide } from "./workflow-view";
@Component({
  selector: "case-workspace",
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: "./case-workspace.component.html",
})
export class CaseWorkspaceComponent {
  @Input({ required: true }) c!: Claim;
  @Input() events: Audit[] = [];
  @Input() actor = "";
  @Input() role = "";
  @Input() busy = false;
  @Input() note = "";
  @Output() command = new EventEmitter<string>();
  @Output() noteChange = new EventEmitter<string>();
  label = label;
  final = isFinal;
  canDecide(c: Claim) {
    return canDecide(c, this.actor, this.role);
  }
}
