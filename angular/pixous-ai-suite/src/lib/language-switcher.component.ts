import { CommonModule } from '@angular/common';
import { Component, Input, OnInit, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AiTranslationService } from './translation.service';
import { SupportedLanguage } from './models';

/**
 * Drop-in language switcher (standalone).
 *
 * Usage:
 *   <pix-language-switcher [languages]="[{code:'en',name:'English'},{code:'ta',name:'Tamil'}]" />
 *
 * If [languages] is omitted it is fetched from GET {apiBaseUrl}/api/translate/languages.
 */
@Component({
  selector: 'pix-language-switcher',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <label class="pix-lang">
      <span *ngIf="label">{{ label }}</span>
      <select [(ngModel)]="selected" (ngModelChange)="onChange($event)">
        <option *ngFor="let l of list" [value]="l.code">{{ l.name }}</option>
      </select>
    </label>
  `,
  styles: [
    `
      .pix-lang { display: inline-flex; align-items: center; gap: 8px; font-family: inherit; }
      .pix-lang select { padding: 6px 10px; border: 1px solid #ddd; border-radius: 8px; }
    `,
  ],
})
export class LanguageSwitcherComponent implements OnInit {
  @Input() label = '';
  @Input() languages: SupportedLanguage[] | null = null;

  protected list: SupportedLanguage[] = [];
  protected selected = 'en';
  private i18n = inject(AiTranslationService);

  ngOnInit(): void {
    this.selected = this.i18n.current();
    if (this.languages?.length) {
      this.list = this.languages;
    } else {
      this.i18n.supportedLanguages().subscribe({
        next: (langs) => (this.list = langs.length ? langs : this.list),
        error: () => undefined,
      });
    }
  }

  onChange(code: string): void {
    this.i18n.use(code);
  }
}
