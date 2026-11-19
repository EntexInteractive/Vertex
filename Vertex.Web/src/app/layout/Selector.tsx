import {useEffect, useRef, useState} from 'react'
import {Icon, type IconName} from '../../components/Icon'

export type SelectorItem = {id: string; label: string}

type SelectorProps = {
    type: string
    icon: IconName
    items: SelectorItem[]
    value: SelectorItem | undefined
    onSelect: (id: string) => void
    /** Shown in place of a value when there is none, e.g. "Not logged in". */
    placeholder: string
}

/** The toolbar's dropdowns (project, branch): a button showing the value and a filterable menu. */
export function Selector({type, icon, items, value, onSelect, placeholder}: SelectorProps) {
    const [open, setOpen] = useState(false)
    const [filter, setFilter] = useState('')
    const root = useRef<HTMLDivElement>(null)

    useEffect(() => {
        if (!open) return
        const close = (event: MouseEvent | KeyboardEvent) => {
            if (event instanceof KeyboardEvent ? event.key === 'Escape' : !root.current?.contains(event.target as Node)) setOpen(false)
        }
        document.addEventListener('mousedown', close)
        document.addEventListener('keydown', close)
        return () => {
            document.removeEventListener('mousedown', close)
            document.removeEventListener('keydown', close)
        }
    }, [open])

    const query = filter.trim().toLocaleLowerCase()
    const visible = items.filter(item => item.label.toLocaleLowerCase().includes(query))

    return (
        <div className="selector-wrap" ref={root}>
            <button
                className="selector"
                type="button"
                aria-expanded={open}
                disabled={items.length === 0}
                onClick={() => {
                    setFilter('')
                    setOpen(current => !current)
                }}
            >
                <span>
                    <small>{type}</small>
                    <b>
                        <Icon name={icon} size={14}/>
                        {value?.label ?? placeholder}
                    </b>
                </span>
                <Icon name="chevron" size={14}/>
            </button>
            {open && (
                <div className="selector-menu">
                    <label>
                        <Icon name="search" size={14}/>
                        <input
                            autoFocus
                            value={filter}
                            onChange={event => setFilter(event.target.value)}
                            onKeyDown={event => {
                                if (event.key === 'Enter' && visible.length > 0) {
                                    onSelect(visible[0].id)
                                    setOpen(false)
                                }
                            }}
                            placeholder={`Filter ${type.toLowerCase()}s`}
                        />
                    </label>
                    {visible.map(item => (
                        <button
                            key={item.id}
                            type="button"
                            className={item.id === value?.id ? 'selected' : ''}
                            onClick={() => {
                                onSelect(item.id)
                                setOpen(false)
                            }}
                        >
                            <Icon name={icon} size={14}/>
                            {item.label}
                            {item.id === value?.id && <Icon name="check" size={14}/>}
                        </button>
                    ))}
                </div>
            )}
        </div>
    )
}
