import type {ReactNode} from 'react'
import {Icon, type IconName} from './Icon'

type ViewFrameProps = {
    eyebrow: string
    title: string
    description: string
    action?: {label: string; icon: IconName; onClick?: () => void; disabled?: boolean}
    /** Marks a page that shows sample data because its feature isn't built yet. */
    preview?: boolean
    children: ReactNode
}

/** The header and body of a full-width page such as History or Tasks. */
export function ViewFrame({eyebrow, title, description, action, preview = false, children}: ViewFrameProps) {
    return (
        <main className="view-frame">
            <header>
                <div>
                    <span>
                        {eyebrow}
                        {preview && <em className="preview-tag" title="This feature isn't built yet; the data is made up.">Preview · sample data</em>}
                    </span>
                    <h1>{title}</h1>
                    <p>{description}</p>
                </div>
                {action && (
                    <button type="button" onClick={action.onClick} disabled={action.disabled}>
                        <Icon name={action.icon} size={15}/>
                        {action.label}
                    </button>
                )}
            </header>
            {children}
        </main>
    )
}
