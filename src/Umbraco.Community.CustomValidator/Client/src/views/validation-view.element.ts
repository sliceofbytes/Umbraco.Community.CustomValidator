import { customElement, state, html, nothing, repeat, LitElement, type PropertyValues } from '@umbraco-cms/backoffice/external/lit';
import { UmbElementMixin } from '@umbraco-cms/backoffice/element-api';
import { UmbTextStyles } from '@umbraco-cms/backoffice/style';
import { UMB_CONTENT_WORKSPACE_CONTEXT } from '@umbraco-cms/backoffice/content';
import { UMB_ACTION_EVENT_CONTEXT } from '@umbraco-cms/backoffice/action';
import { UmbEntityUpdatedEvent } from '@umbraco-cms/backoffice/entity-action';
import { VALIDATION_WORKSPACE_CONTEXT } from '../contexts/validation-workspace-context.js';
import type { ValidationResult, NotificationColor, ValidationMessage } from '../validation/types.js';
import { ValidationSeverity } from '../validation/types.js';

const SAVE_DELAY_MS = 500;
const INITIAL_VALIDATION_DELAY_MS = 500;

const SEVERITY_ORDER: Record<ValidationSeverity, number> = {
    [ValidationSeverity.Error]: 0,
    [ValidationSeverity.Warning]: 1,
    [ValidationSeverity.Info]: 2
} as const;

const SEVERITY_COLOR_MAP: Record<ValidationSeverity, NotificationColor> = {
    [ValidationSeverity.Error]: 'danger',
    [ValidationSeverity.Warning]: 'warning',
    [ValidationSeverity.Info]: 'default'
} as const;

@customElement('custom-validator-workspace-view')
export class CustomValidatorWorkspaceView extends UmbElementMixin(LitElement) {
    #countContext?: typeof VALIDATION_WORKSPACE_CONTEXT.TYPE;
    #contentWorkspace?: typeof UMB_CONTENT_WORKSPACE_CONTEXT.TYPE;
    #actionEventContext?: typeof UMB_ACTION_EVENT_CONTEXT.TYPE;
    #currentDocumentId?: string;
    #variantObserverSetup = false;
    #instanceIndexAssigned = false;

    @state()
    private instanceCount = 0;
    
    @state()
    private _documentId?: string;
    
    @state()
    private _validationResult?: ValidationResult;
    
    @state()
    private _isValidating = false;
    
    @state()
    private _currentCulture?: string;
    
    @state()
    private _cultureReady = false;
    
    constructor() {
        super();
        
        this.consumeContext(VALIDATION_WORKSPACE_CONTEXT, (instance) => {
            if (!instance) return;
            
            this.#countContext = instance;
            
            this.observe(instance.instanceCounter, (count) => {
                if (!this.#instanceIndexAssigned) {
                    this.#instanceIndexAssigned = true;
                    this.instanceCount = count;
                    
                    instance.incrementInstance();
                    this.#trySetupVariantObserver();
                }
            });
        });
        
        // Listen for save/publish events
        this.consumeContext(UMB_ACTION_EVENT_CONTEXT, (context) => {
            if (!context) return;
            
            this.#actionEventContext = context;
            this.#actionEventContext.addEventListener(
                UmbEntityUpdatedEvent.TYPE,
                this.#onEntityUpdated
            );
        });
        
        this.#setupWorkspaceObservers();
        this.#setupValidationObservers();
        window.addEventListener('custom-validator:validate-all', this.#onGlobalValidateAll);
    }

    #onEntityUpdated = async (event: Event) => {
        if (!(event instanceof UmbEntityUpdatedEvent)) return;
        
        const eventUnique = event.getUnique();
        const documentUnique = this.#contentWorkspace?.getUnique();
        
        // Check if this event is for our current document
        if (eventUnique === documentUnique && this._documentId) {

            // Brief delay to ensure backend cache is cleared
            await this.#delay(300);
            await this.#validateAndUpdateResult({ skipSave: true });
        }
    };
    
    #setupWorkspaceObservers() {
        this.consumeContext(UMB_CONTENT_WORKSPACE_CONTEXT, (workspace) => {
            if (!workspace) return;
            this.#contentWorkspace = workspace;
            this.#observeDocumentChanges(workspace);
            
            // Try to set up variant observer once we have the workspace
            this.#trySetupVariantObserver();
        });
    }
    
    #trySetupVariantObserver() {
        // Only set up once, and only when both workspace and index are assigned
        if (this.#variantObserverSetup || !this.#contentWorkspace || !this.#instanceIndexAssigned) {
            return;
        }
        
        this.#variantObserverSetup = true;
        this.#observeVariant(this.#contentWorkspace);
    }
    
    #observeVariant(workspace: typeof UMB_CONTENT_WORKSPACE_CONTEXT.TYPE) {
        
        this.observe(
            workspace.splitView.activeVariantByIndex(this.instanceCount),
            async (variant) => {

                const newCulture = variant?.culture ?? undefined;
                const cultureChanged = this._currentCulture !== newCulture;
                this._currentCulture = newCulture;
                
                if (!this._cultureReady) {
                    this._cultureReady = true;
                }
                
                // Validate on culture change OR first load (no result yet)
                if (this._documentId && (cultureChanged || !this._validationResult)) {
                    await this.#validateAndUpdateResult({ useDelay: true, skipSave: true });
                }
            }
        );
    }

    #observeDocumentChanges(workspace: typeof UMB_CONTENT_WORKSPACE_CONTEXT.TYPE) {
        this.observe(
            workspace.unique,
            (unique) => {
                const isDocumentSwitch = this.#isDocumentSwitch(unique);
                
                // Clear UI state when switching to a different document
                if (isDocumentSwitch) {
                    this._validationResult = undefined;
                }
                
                // Update tracked document ID
                this._documentId = unique ?? undefined;
                this.#currentDocumentId = unique ?? undefined;
            }
        );
    }

    #isDocumentSwitch(newDocumentId: string | null | undefined): boolean {
        return this.#currentDocumentId !== undefined && this.#currentDocumentId !== newDocumentId;
    }

    #setupValidationObservers() {
        this.consumeContext(VALIDATION_WORKSPACE_CONTEXT, (validationContext) => {
            if (!validationContext) return;

            this.observe(
                validationContext.isValidating,
                (isValidating) => {
                    this._isValidating = isValidating;
                }
            );
        });
    }

    override disconnectedCallback() {
        super.disconnectedCallback();
        window.removeEventListener('custom-validator:validate-all', this.#onGlobalValidateAll);
        
        // Clean up action event listener
        if (this.#actionEventContext) {
            this.#actionEventContext.removeEventListener(
                UmbEntityUpdatedEvent.TYPE,
                this.#onEntityUpdated // Remove the "as EventListener"
            );
        }
        
        if (this.#countContext) {
            this.#countContext.resetInstanceCounter();
        }
    }

    // Unified validation method for all cultures
    async #validateAndUpdateResult(options: { useDelay?: boolean; skipSave?: boolean } = {}) {
        if (!this._documentId) return;

        try {
            const validationContext = await this.getContext(VALIDATION_WORKSPACE_CONTEXT);
            if (!validationContext) return;

            const performValidation = async () => {
                try {
                    if (!options.skipSave && this.#contentWorkspace?.requestSubmit) {
                        await this.#contentWorkspace.requestSubmit();
                        await this.#delay(SAVE_DELAY_MS);
                    }

                    const result = await validationContext.validateManually(this._documentId!, this._currentCulture);
                    this._validationResult = result;

                } catch (error) {
                    console.error('Validation failed:', error);
                    
                    // Show error state in UI instead of just logging
                    this._validationResult = {
                        contentId: this._documentId!,
                        hasValidator: false,
                        messages: [{
                            message: 'Validation failed. Please check the logs.',
                            severity: 'Error' as any
                        }]
                    };
                }
            };

            if (options.useDelay) {
                await this.#delay(INITIAL_VALIDATION_DELAY_MS);
            }

            await performValidation();
        } catch (error) {
            console.error('Failed to validate and update result:', error);
        }
    }

    #handleValidateClick = async () => {
        // Validate this pane with save
        await this.#validateAndUpdateResult({ skipSave: false });
        // Dispatch global event so all panes validate (but skip save in others)
        window.dispatchEvent(new CustomEvent('custom-validator:validate-all', { detail: { skipSave: true } }));
    };

    #onGlobalValidateAll = async (event: Event) => {
        // Only validate if this pane is attached and has a document
        if (this.isConnected && this._documentId) {
            // If event has detail.skipSave, use it; otherwise default to true
            let skipSave = true;
            if (event instanceof CustomEvent && typeof event.detail?.skipSave === 'boolean') {
                skipSave = event.detail.skipSave;
            }
            await this.#validateAndUpdateResult({ skipSave });
        }
    }

    #getMessageCounts(): { errors: number; warnings: number } {
        if (!this._validationResult) {
            return { errors: 0, warnings: 0 };
        }
        return {
            errors: this._validationResult.messages.filter(m => m.severity === ValidationSeverity.Error).length,
            warnings: this._validationResult.messages.filter(m => m.severity === ValidationSeverity.Warning).length
        };
    }

    #delay(ms: number): Promise<void> {
        return new Promise(resolve => setTimeout(resolve, ms));
    }

    #getSeverityColor(severity: ValidationSeverity): NotificationColor {
        return SEVERITY_COLOR_MAP[severity];
    }

    #renderValidationResults() {

        if (this._isValidating || !this._validationResult) {
            return this.#renderLoadingState();
        }

        if (!this._validationResult.hasValidator) {
            return html`
                <uui-box headline="Status" headline-variant="h5">
                    <p>No custom validation configured for this document.</p>
                </uui-box>`;
        }

        const hasErrorsOrWarnings = this._validationResult.messages.some(
            m => m.severity === ValidationSeverity.Error || m.severity === ValidationSeverity.Warning);

        return html`
            <uui-box headline="Validation Results" headline-variant="h5">
                ${!hasErrorsOrWarnings ? this.#renderSuccessMessage() : nothing}
                ${this._validationResult.messages.length > 0 ? this.#renderMessagesTable(this._validationResult.messages) : nothing}
            </uui-box>
        `;
    }

    #renderMessagesTable(messages: ValidationMessage[]): unknown {

        const sortedMessages = [...messages].sort((a, b) => SEVERITY_ORDER[a.severity] - SEVERITY_ORDER[b.severity]);

        return html`
        <uui-table aria-label="Validation Messages">
                    <uui-table-head>
                        <uui-table-head-cell style="width: 120px;">Severity</uui-table-head-cell>
                        <uui-table-head-cell>Message</uui-table-head-cell>
                    </uui-table-head>
                    ${repeat(
                        sortedMessages ?? [],
                        (msg) => msg.message,
                        (msg) => html`
                            <uui-table-row>
                                <uui-table-cell>
                                    <uui-tag color=${this.#getSeverityColor(msg.severity)} look="primary">
                                        ${msg.severity}
                                    </uui-tag>
                                </uui-table-cell>
                                <uui-table-cell>${msg.message}</uui-table-cell>
                            </uui-table-row>
                        `
                    )}
        </uui-table>`;
    }

    #renderLoadingState() {
        return html`
            <uui-box headline="Status" headline-variant="h5">
                <div style="display: flex; align-items: center; gap: var(--uui-size-space-3);">
                    <uui-loader></uui-loader>
                    <span>Validating...</span>
                </div>
            </uui-box>
        `;
    }

    #renderSuccessMessage() {
        return html`
            <p style="color: var(--uui-color-positive);">
                <uui-icon name="icon-check"></uui-icon>
                All validations passed successfully.
            </p>
        `;
    }

    #renderHeader() {
        const { errors, warnings } = this.#getMessageCounts();

        return html`
            <div slot="headline">
                ${this.#renderHeaderIcon(errors)}
                Document Validation
            </div>
            <div slot="header-actions">
                ${errors > 0 ? html`
                    <uui-tag color="danger" look="primary">${errors}</uui-tag>
                ` : nothing}
                ${warnings > 0 ? html`
                    <uui-tag color="warning" look="primary">${warnings}</uui-tag>
                ` : nothing}
            </div>
        `;
    }

    #renderHeaderIcon(errorCount: number) {
        return errorCount > 0 
            ? html`<uui-icon name="icon-delete" style="color: var(--uui-color-danger);"></uui-icon>`
            : html`<uui-icon name="icon-check" style="color: var(--uui-color-positive);"></uui-icon>`;
    }

    #renderControls() {
        return html`
            <uui-button-group>
                <uui-button
                    look="primary"
                    color="default"
                    label="Save & Validate"
                    @click=${this.#handleValidateClick}
                    ?disabled=${!this._documentId || this._isValidating}>
                    Save & Validate
                </uui-button>
                ${this._isValidating ? html`<uui-loader></uui-loader>` : nothing}
            </uui-button-group>
        `;
    }

    override render() {
        // Show controls if any culture has a validator
        const shouldShowControls = this._validationResult?.hasValidator !== false && this._validationResult !== undefined;
        
        return html`
            <umb-body-layout header-transparent header-fit-height>
                <div style="display: flex; flex-direction: column; gap: var(--uui-size-layout-1);">
                    ${shouldShowControls ? html`
                        <uui-box headline-variant="h4">
                            ${this.#renderHeader()}
                            ${this.#renderControls()}
                        </uui-box>
                    ` : nothing}

                    ${this.#renderValidationResults()}
                </div>
            </umb-body-layout>
        `;
    }

    static override styles = [UmbTextStyles];
}

export { CustomValidatorWorkspaceView as element };

declare global {
    interface HTMLElementTagNameMap {
        'custom-validator-workspace-view': CustomValidatorWorkspaceView;
    }
}
