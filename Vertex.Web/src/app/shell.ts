import {createContext, useContext} from 'react'

export type ShellDialog = 'clone' | 'new'

export type ShellState = {
    openPalette: () => void
    openDialog: (dialog: ShellDialog) => void
    /** Moves focus to the submit message box on the Changes page. */
    focusSubmit: () => void
    registerSubmitFocus: (focus: (() => void) | null) => void
}

export const ShellContext = createContext<ShellState | null>(null)

/** Shell-wide actions: the command palette and the workspace dialogs. */
export function useShell(): ShellState {
    const state = useContext(ShellContext)
    if (!state) throw new Error('useShell must be used inside AppLayout.')
    return state
}
